using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Tenants;
using CaeManager.Infrastructure.Coordinacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Documentos;

/// <summary>
/// Ejecuta al arrancar la conciliación de la regla A-1 (<see cref="IConciliacionAcreditacionesEmpresaService"/>) en
/// todos los Tenants, hasta completar una pasada. Solo se registra si
/// <see cref="ConciliacionAcreditacionesEmpresaOptions.EjecutarAlArrancar"/> está encendido: apagado por defecto.
///
/// <para>
/// <b>Un Tenant propietario cada vez</b>, con el mismo mecanismo que <c>CierreConversacionesResueltasHostedService</c>:
/// recorre todos los Tenants (también los suspendidos) y abre un <see cref="AmbitoTenantExplicito"/> y un
/// <c>CreateScope()</c> por cada uno. La conexión es <c>cae_app_runtime</c> y la RLS solo deja ver y escribir las filas
/// de ese Tenant; nunca una identidad que se salte la RLS, ni lecturas entre Tenants. Las acreditaciones se auditan
/// como Sistema.
/// </para>
///
/// <para>
/// <b>Hasta completarla.</b> Envuelto en elección de líder, como <c>CierreMensualCuadroDireccionHostedService</c>: en el
/// relevo azul/verde, o con varias réplicas, solo una instancia la ejecuta a la vez. La que no obtiene el liderazgo no
/// se rinde: vuelve a intentarlo tras <see cref="ConciliacionAcreditacionesEmpresaOptions.EsperaEntreIntentos"/>, de modo
/// que si la líder se detiene a mitad (su bloqueo se libera con la conexión), otra retoma la pasada. Una pasada con un
/// Tenant o un lote fallido tampoco se da por terminada: se registra como error y se repite. Cada instancia para al
/// completar una pasada propia o al agotar
/// <see cref="ConciliacionAcreditacionesEmpresaOptions.IntentosMaximos"/>. Sin marcador persistente de pasada
/// completada: una segunda instancia hace, a lo sumo, una pasada más, que por ser idempotente no crea nada que ya exista.
/// </para>
/// </summary>
public class ConciliacionAcreditacionesEmpresaHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    ConciliacionAcreditacionesEmpresaOptions opciones,
    TimeProvider reloj,
    ILogger<ConciliacionAcreditacionesEmpresaHostedService> logger)
    : BackgroundService
{
    internal const string ClaveLider = "conciliacion-acreditaciones-empresa";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();
        try
        {
            await ConciliarHastaCompletarAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Intenta la pasada como líder hasta completarla o agotar los intentos. Devuelve si esta instancia completó una.
    /// Interno para probar el reintento sin host.
    /// </summary>
    internal Task<bool> ConciliarHastaCompletarAsync(CancellationToken stoppingToken) =>
        ConciliarHastaCompletarAsync(ProcesarAsync, stoppingToken);

    /// <summary>El mismo bucle con la pasada que se indique: el test del reintento la sustituye.</summary>
    internal async Task<bool> ConciliarHastaCompletarAsync(
        Func<CancellationToken, Task<ResultadoPasadaConciliacion>> pasada, CancellationToken stoppingToken)
    {
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();
        var intentos = Math.Max(1, opciones.IntentosMaximos);

        for (var intento = 1; intento <= intentos; intento++)
        {
            if (await IntentarPasadaComoLiderAsync(pasada, intento, stoppingToken))
                return true;

            if (intento < intentos)
                await Task.Delay(opciones.EsperaEntreIntentos, reloj, stoppingToken);
        }

        logger.LogError(
            "La conciliación de acreditaciones de Documentos de Empresa no se completó en {Intentos} intentos; queda sin terminar hasta otro arranque con el disparador encendido.",
            intentos);
        return false;
    }

    private async Task<bool> IntentarPasadaComoLiderAsync(
        Func<CancellationToken, Task<ResultadoPasadaConciliacion>> pasada, int intento, CancellationToken stoppingToken)
    {
        try
        {
            ResultadoPasadaConciliacion? resultado = null;
            var esLider = await eleccionLider.IntentarEjecutarComoLiderAsync(
                ClaveLider, async ct => resultado = await pasada(ct), stoppingToken);
            if (!esLider)
            {
                logger.LogInformation(
                    "Conciliación de acreditaciones de Documentos de Empresa: otra instancia la está ejecutando (intento {Intento}); se reintentará.",
                    intento);
                return false;
            }

            return resultado is { Completa: true };
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Falló la conciliación de acreditaciones de Documentos de Empresa (intento {Intento}); se reintentará.", intento);
            return false;
        }
    }

    /// <summary>
    /// Una pasada sobre todos los Tenants, uno a uno. Un Tenant o un lote que falla no detiene al resto, pero deja la
    /// pasada incompleta: se registra como error, no como terminada. Interno para que un test bajo
    /// <c>cae_app_runtime</c> ejercite este mismo camino, interceptores incluidos, sin depender de la elección de líder.
    /// </summary>
    internal async Task<ResultadoPasadaConciliacion> ProcesarAsync(CancellationToken stoppingToken)
    {
        // Se repite aquí, además de en ConciliarHastaCompletarAsync, para que quien llame a este método directamente
        // observe también el actor Sistema.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        List<Guid> tenants;
        using (var ambito = ambitoFactory.CreateScope())
        {
            tenants = await ambito.ServiceProvider.GetRequiredService<ITenantsQueryContext>()
                .Tenants.Select(t => t.Id)
                .ToListAsync(stoppingToken);
        }

        return await ConciliarTenantsAsync(tenants, stoppingToken);
    }

    /// <summary>La pasada sobre unos Tenants dados. Interno para probar el resultado agregado sin base de datos.</summary>
    internal async Task<ResultadoPasadaConciliacion> ConciliarTenantsAsync(
        IReadOnlyList<Guid> tenants, CancellationToken stoppingToken)
    {
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        var creadas = 0;
        var lotesFallidos = 0;
        var tenantsIncompletos = new List<Guid>();
        foreach (var tenantId in tenants)
        {
            stoppingToken.ThrowIfCancellationRequested();

            try
            {
                var resultado = await ConciliarTenantPropietarioAsync(tenantId, stoppingToken);
                creadas += resultado.Creadas;
                if (resultado.Completa)
                    continue;

                lotesFallidos += resultado.LotesFallidos;
                tenantsIncompletos.Add(tenantId);
                foreach (var error in resultado.Errores)
                    logger.LogError(error,
                        "Falló un lote de la conciliación de acreditaciones de Documentos de Empresa del Tenant propietario {TenantId}; se continúa con el resto.",
                        tenantId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                tenantsIncompletos.Add(tenantId);
                logger.LogError(ex,
                    "Falló la conciliación de acreditaciones de Documentos de Empresa del Tenant propietario {TenantId}; se continúa con el resto.",
                    tenantId);
            }
        }

        var pasada = new ResultadoPasadaConciliacion(tenants.Count, tenantsIncompletos, lotesFallidos, creadas);
        if (pasada.Completa)
            logger.LogInformation(
                "Conciliación de acreditaciones de Documentos de Empresa terminada: {Creadas} creadas en {Tenants} Tenants.",
                creadas, tenants.Count);
        else
            logger.LogError(
                "Conciliación de acreditaciones de Documentos de Empresa incompleta: {Creadas} creadas; {Incompletos} de {Tenants} Tenants sin terminar ({LotesFallidos} lotes fallidos): {TenantsIncompletos}.",
                creadas, tenantsIncompletos.Count, tenants.Count, lotesFallidos, tenantsIncompletos);
        return pasada;
    }

    private async Task<ResultadoConciliacionTenant> ConciliarTenantPropietarioAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);
        using var ambito = ambitoFactory.CreateScope();

        var resultado = await ambito.ServiceProvider.GetRequiredService<IConciliacionAcreditacionesEmpresaService>()
            .ConciliarTenantEnCursoAsync(stoppingToken);
        if (resultado.Creadas > 0)
            logger.LogInformation(
                "Conciliación de acreditaciones de Documentos de Empresa: {Creadas} creadas en el Tenant propietario {TenantId}.",
                resultado.Creadas, tenantId);
        return resultado;
    }
}

/// <summary>Lo que dejó una pasada de la conciliación sobre todos los Tenants.</summary>
/// <param name="Tenants">Tenants recorridos.</param>
/// <param name="TenantsIncompletos">Tenants propietarios con un fallo propio o con algún lote fallido.</param>
/// <param name="LotesFallidos">Lotes fallidos, sumados entre Tenants.</param>
/// <param name="Creadas">
/// Suma de <see cref="ResultadoConciliacionTenant.Creadas"/>: una cota superior de las acreditaciones insertadas, solo
/// para el registro.
/// </param>
public sealed record ResultadoPasadaConciliacion(int Tenants, IReadOnlyList<Guid> TenantsIncompletos, int LotesFallidos, int Creadas)
{
    public bool Completa => TenantsIncompletos.Count == 0;
}
