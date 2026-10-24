using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Coordinacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <summary>
/// Pasada de medianoche, reposición al arrancar y cotejo nocturno del estado materializado (diseño § 2.2, § 7.2 y § 13.12),
/// con <see cref="CambioDeDiaEstadoMaterializado"/>.
///
/// <para>
/// <b>Medianoche de Madrid.</b> El estado por vigencia cambia una sola vez al día, a las 00:00 de Madrid, no de UTC
/// (decisión de producto del 2026-10-24). La espera hasta la comprobación siguiente se calcula con
/// <see cref="DiaDeNegocio.InicioEnUtc"/> del día siguiente, que sigue el horario de verano y de invierno: la pasada
/// empieza en el primer segundo del día nuevo de Madrid, a las 22:00 o a las 23:00 UTC según la época.
/// </para>
///
/// <para>
/// <b>Reposición.</b> El primer ciclo corre al arrancar, y después uno cada <see cref="IntervaloMaximo"/> como mucho: un
/// Tenant cuya marca <c>UltimoDiaProcesado</c> es anterior a hoy (servidor parado a medianoche, pasada fallida o aplazada
/// por la cola) se repone en cuanto se puede. Con la marca al día un ciclo solo lee la marca de cada Tenant.
/// </para>
///
/// <para>
/// <b>Una sola instancia</b>, con elección de líder (<see cref="IEleccionLiderService"/>, cerrojo de asesoramiento de sesión),
/// como <see cref="ColaEstadoMaterializadoHostedService"/>: durante el relevo azul/verde solo procesa la ranura con el
/// cerrojo. Aunque las dos procesaran a la vez, el encolado no duplica entradas, la cola reclama con <c>SKIP LOCKED</c>, el
/// cotejo solo lee y la marca solo avanza.
/// </para>
///
/// <para>
/// <b>Un Tenant propietario cada vez</b>, con el mismo contrato de identidad que la cola: un <see cref="AmbitoTenantExplicito"/>
/// y un <c>CreateScope()</c> por Tenant, con la conexión <c>cae_app_runtime</c> bajo la RLS forzada. Ninguna identidad
/// salta la RLS.
/// </para>
/// </summary>
public class CambioDeDiaEstadoMaterializadoHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    TimeProvider reloj,
    ILogger<CambioDeDiaEstadoMaterializadoHostedService> logger)
    : BackgroundService
{
    /// <summary>Clave de la elección de líder.</summary>
    public const string ClaveDeLider = "estado-materializado-cambio-de-dia";

    /// <summary>Espera máxima entre dos ciclos: lo que tarda en reintentarse un cambio de día aplazado.</summary>
    public static readonly TimeSpan IntervaloMaximo = TimeSpan.FromMinutes(5);

    /// <summary>Margen tras la medianoche de Madrid, para que el ciclo caiga ya en el día nuevo.</summary>
    public static readonly TimeSpan MargenTrasMedianoche = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Cuánto esperar desde <paramref name="ahora"/> hasta el ciclo siguiente: hasta el primer segundo del día siguiente de
    /// Madrid, o <see cref="IntervaloMaximo"/> si llega antes.
    /// </summary>
    public static TimeSpan EsperaHastaLaSiguienteComprobacion(DateTimeOffset ahora)
    {
        var medianoche = new DateTimeOffset(DiaDeNegocio.InicioEnUtc(DiaDeNegocio.De(ahora).AddDays(1)), TimeSpan.Zero);
        var hastaMedianoche = medianoche - ahora + MargenTrasMedianoche;
        var espera = hastaMedianoche < IntervaloMaximo ? hastaMedianoche : IntervaloMaximo;
        return espera < MargenTrasMedianoche ? MargenTrasMedianoche : espera;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // P41c: todo lo que hace este servicio de fondo lo hace la propia plataforma (ver también ProcesarAsync).
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        while (!stoppingToken.IsCancellationRequested)
        {
            await EjecutarCicloAsync(stoppingToken);
            try
            {
                await Task.Delay(EsperaHastaLaSiguienteComprobacion(reloj.GetUtcNow()), reloj, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task EjecutarCicloAsync(CancellationToken stoppingToken)
    {
        try
        {
            await eleccionLider.IntentarEjecutarComoLiderAsync(ClaveDeLider, ProcesarAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo del cambio de día del estado materializado.");
        }
    }

    /// <summary>
    /// Un ciclo sobre todos los Tenants, uno a uno, con el día de negocio de ahora (el mismo reloj que el recálculo). Interno
    /// para que los tests bajo <c>cae_app_runtime</c> ejerciten este mismo camino sin depender de la elección de líder ni de
    /// la espera.
    /// </summary>
    internal async Task<IReadOnlyDictionary<Guid, ResultadoCambioDeDia>> ProcesarAsync(CancellationToken stoppingToken)
    {
        // Se repite aquí, además de en ExecuteAsync, para que el test que llama a este método observe el mismo actor.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        var hoy = DiaDeNegocio.Hoy();
        List<Guid> tenants;
        using (var ambito = ambitoFactory.CreateScope())
        {
            tenants = await ambito.ServiceProvider.GetRequiredService<ITenantsQueryContext>()
                .Tenants.Select(t => t.Id)
                .ToListAsync(stoppingToken);
        }

        var resultados = new Dictionary<Guid, ResultadoCambioDeDia>();
        foreach (var tenantId in tenants)
        {
            stoppingToken.ThrowIfCancellationRequested();

            // Aislado por Tenant: un fallo no deja sin cambio de día al resto, y el ciclo siguiente lo reintenta.
            try
            {
                using var _ = AmbitoTenantExplicito.Establecer(tenantId);
                using var ambito = ambitoFactory.CreateScope();
                resultados[tenantId] = await ambito.ServiceProvider.GetRequiredService<CambioDeDiaEstadoMaterializado>()
                    .ProcesarTenantAsync(hoy, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falló el cambio de día del estado materializado del Tenant propietario {TenantId}; se continúa con el resto.", tenantId);
            }
        }

        return resultados;
    }
}
