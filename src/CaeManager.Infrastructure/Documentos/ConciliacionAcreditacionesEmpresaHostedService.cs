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
/// Ejecuta una vez, al arrancar, la conciliación de la regla A-1
/// (<see cref="IConciliacionAcreditacionesEmpresaService"/>) en todos los Tenants. Solo se registra si
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
/// Envuelto en elección de líder: en el relevo azul/verde, o con varias réplicas, solo una instancia la ejecuta. Un
/// fallo en un Tenant no detiene al resto; lo que falte lo cubre la pasada siguiente, porque es idempotente.
/// </para>
/// </summary>
public class ConciliacionAcreditacionesEmpresaHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    ILogger<ConciliacionAcreditacionesEmpresaHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        try
        {
            var ejecutada = await eleccionLider.IntentarEjecutarComoLiderAsync(
                "conciliacion-acreditaciones-empresa", ProcesarAsync, stoppingToken);
            if (!ejecutada)
                logger.LogInformation(
                    "Conciliación de acreditaciones de Documentos de Empresa: otra instancia la ejecuta; esta no hace nada.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló la conciliación de acreditaciones de Documentos de Empresa.");
        }
    }

    /// <summary>
    /// Una pasada sobre todos los Tenants, uno a uno. Interno para que un test bajo <c>cae_app_runtime</c> ejercite este
    /// mismo camino, interceptores incluidos, sin depender de la elección de líder.
    /// </summary>
    internal async Task ProcesarAsync(CancellationToken stoppingToken)
    {
        // Se repite aquí, además de en ExecuteAsync, para que quien llame a este método sin pasar por ExecuteAsync
        // observe también el actor Sistema.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        List<Guid> tenants;
        using (var ambito = ambitoFactory.CreateScope())
        {
            tenants = await ambito.ServiceProvider.GetRequiredService<ITenantsQueryContext>()
                .Tenants.Select(t => t.Id)
                .ToListAsync(stoppingToken);
        }

        var total = 0;
        foreach (var tenantId in tenants)
        {
            stoppingToken.ThrowIfCancellationRequested();

            try
            {
                total += await ConciliarTenantPropietarioAsync(tenantId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falló la conciliación de acreditaciones de Documentos de Empresa del Tenant propietario {TenantId}; se continúa con el resto.",
                    tenantId);
            }
        }

        logger.LogInformation(
            "Conciliación de acreditaciones de Documentos de Empresa terminada: {Creadas} creadas en {Tenants} Tenants.",
            total, tenants.Count);
    }

    private async Task<int> ConciliarTenantPropietarioAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);
        using var ambito = ambitoFactory.CreateScope();

        var creadas = await ambito.ServiceProvider.GetRequiredService<IConciliacionAcreditacionesEmpresaService>()
            .ConciliarTenantEnCursoAsync(stoppingToken);
        if (creadas > 0)
            logger.LogInformation(
                "Conciliación de acreditaciones de Documentos de Empresa: {Creadas} creadas en el Tenant propietario {TenantId}.",
                creadas, tenantId);
        return creadas;
    }
}
