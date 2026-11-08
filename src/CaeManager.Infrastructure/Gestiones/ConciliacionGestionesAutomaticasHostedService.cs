using CaeManager.Application.Common;
using CaeManager.Application.Gestiones;
using CaeManager.Application.Tenants;
using CaeManager.Infrastructure.Coordinacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Gestiones;

/// <summary>
/// Ejecuta la conciliación de Gestiones automáticas (<see cref="IConciliacionGestionesAutomaticasService"/>) en todos
/// los Tenants, tras arrancar, hasta completar una pasada. Solo se registra si
/// <see cref="ConciliacionGestionesAutomaticasOptions.EjecutarAlArrancar"/> está encendido: apagado por defecto.
///
/// <para>
/// <b>Un Tenant propietario cada vez</b>, con el mismo mecanismo que <c>CierreConversacionesResueltasHostedService</c>:
/// recorre todos los Tenants (también los suspendidos) y abre un <see cref="AmbitoTenantExplicito"/> por cada uno. La
/// conexión es <c>cae_app_runtime</c> y la RLS solo deja ver y escribir las filas de ese Tenant; nunca una identidad que
/// se salte la RLS, ni lecturas entre Tenants. Las Gestiones se auditan como Sistema.
/// </para>
///
/// <para>
/// <b>Un ámbito de servicios por lote.</b> Un lote fallido no corta el resto: su contexto se descarta con lo que tuviera
/// rastreado, y el siguiente lote empieza limpio. Igual con un Tenant cuya búsqueda falla. La pasada cuenta lotes y
/// Tenants fallidos (<see cref="ResultadoPasada"/>) y solo es completa sin ninguno.
/// </para>
///
/// <para>
/// <b>Hasta completar.</b> Cada intento pide el liderazgo: con varias instancias, solo una concilia a la vez. Si esta
/// instancia no lo obtiene, o su pasada no se completó, lo reintenta tras
/// <see cref="ConciliacionGestionesAutomaticasOptions.IntervaloReintento"/>: si el líder cae a mitad, otra instancia
/// retoma, y la pasada que repite lo ya conciliado no crea nada. Termina con su primera pasada completa, o al agotar
/// <see cref="ConciliacionGestionesAutomaticasOptions.MaximoIntentos"/> (entonces lo registra como error).
/// </para>
/// </summary>
public class ConciliacionGestionesAutomaticasHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    IOptions<ConciliacionGestionesAutomaticasOptions> opciones,
    TimeProvider reloj,
    ILogger<ConciliacionGestionesAutomaticasHostedService> logger)
    : BackgroundService
{
    internal const string ClaveLiderazgo = "conciliacion-gestiones-automaticas";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();
        await EjecutarHastaCompletarAsync(stoppingToken);
    }

    /// <summary>
    /// El bucle de intentos. Interno para que un test lo ejercite sin el host. Devuelve la pasada completa, o null si
    /// no llegó a completarse ninguna.
    /// </summary>
    internal async Task<ResultadoPasada?> EjecutarHastaCompletarAsync(CancellationToken stoppingToken)
    {
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        try
        {
            // Dentro del try: una sección mal escrita (un TimeSpan ilegible) o una ventana que desborda la fecha se
            // registran en vez de dejar fallida la tarea del servicio, que pararía el host.
            var config = opciones.Value;

            // La ventana se ancla al arranque, no a cada intento: los reintentos miran los mismos Documentos.
            var desdeUtc = reloj.GetUtcNow().UtcDateTime - config.Ventana;

            await Task.Delay(config.EsperaInicial, reloj, stoppingToken);

            for (var intento = 1; intento <= config.MaximoIntentos; intento++)
            {
                ResultadoPasada? resultado = null;
                bool lider;
                try
                {
                    lider = await eleccionLider.IntentarEjecutarComoLiderAsync(
                        ClaveLiderazgo, async t => resultado = await ProcesarAsync(desdeUtc, t), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lider = true;
                    logger.LogError(ex, "Falló el intento {Intento} de conciliación de Gestiones automáticas.", intento);
                }

                if (resultado is { Completa: true })
                {
                    logger.LogInformation(
                        "Conciliación de Gestiones automáticas completa: {Creadas} creadas en {Tenants} Tenants y {Lotes} lotes.",
                        resultado.Creadas, resultado.Tenants, resultado.Lotes);
                    return resultado;
                }

                if (!lider)
                    logger.LogInformation(
                        "Conciliación de Gestiones automáticas: otra instancia la está ejecutando; esta lo reintentará.");
                else if (resultado is not null)
                    logger.LogWarning(
                        "Conciliación de Gestiones automáticas incompleta: {Creadas} creadas, {TenantsFallidos} de {Tenants} Tenants y {LotesFallidos} de {Lotes} lotes fallidos; se reintentará.",
                        resultado.Creadas, resultado.TenantsFallidos, resultado.Tenants, resultado.LotesFallidos, resultado.Lotes);

                if (intento < config.MaximoIntentos)
                    await Task.Delay(config.IntervaloReintento, reloj, stoppingToken);
            }

            logger.LogError(
                "La conciliación de Gestiones automáticas no completó ninguna pasada en {Intentos} intentos.", config.MaximoIntentos);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Nunca tumba el host: una configuración inválida (ilegible, una espera negativa o una ventana que desborda
            // la fecha) o un fallo inesperado se registran.
            logger.LogError(ex, "La conciliación de Gestiones automáticas se detuvo por un error.");
        }

        return null;
    }

    /// <summary>
    /// Una pasada sobre todos los Tenants, uno a uno. Interno para que un test bajo <c>cae_app_runtime</c> ejercite este
    /// mismo camino, interceptores incluidos, sin depender de la elección de líder ni de las esperas.
    /// </summary>
    internal async Task<ResultadoPasada> ProcesarAsync(DateTime desdeUtc, CancellationToken stoppingToken)
    {
        // Se repite aquí para que quien llame a este método sin pasar por el bucle observe también el actor Sistema.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        var tenants = await ListarTenantsAsync(stoppingToken);

        var resultado = new ResultadoPasada { Tenants = tenants.Count };
        foreach (var tenantId in tenants)
        {
            stoppingToken.ThrowIfCancellationRequested();
            await ConciliarTenantPropietarioAsync(tenantId, desdeUtc, resultado, stoppingToken);
        }

        return resultado;
    }

    /// <summary>Todos los Tenants, también los suspendidos. Virtual para que un test de la orquestación fije la lista.</summary>
    protected internal virtual async Task<IReadOnlyList<Guid>> ListarTenantsAsync(CancellationToken stoppingToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        return await ambito.ServiceProvider.GetRequiredService<ITenantsQueryContext>()
            .Tenants.Select(t => t.Id)
            .OrderBy(id => id)
            .ToListAsync(stoppingToken);
    }

    private async Task ConciliarTenantPropietarioAsync(
        Guid tenantId, DateTime desdeUtc, ResultadoPasada resultado, CancellationToken stoppingToken)
    {
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);

        IReadOnlyList<Guid> sinGestion;
        try
        {
            using var ambito = ambitoFactory.CreateScope();
            sinGestion = await ambito.ServiceProvider.GetRequiredService<IConciliacionGestionesAutomaticasService>()
                .BuscarDocumentosSinGestionAsync(desdeUtc, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            resultado.TenantsFallidos++;
            logger.LogError(ex,
                "Falló la búsqueda de Documentos sin Gestión automática del Tenant propietario {TenantId}; se continúa con el resto.",
                tenantId);
            return;
        }

        var creadasEnElTenant = 0;
        foreach (var lote in sinGestion.Chunk(ConciliacionGestionesAutomaticasService.DocumentosPorLote))
        {
            stoppingToken.ThrowIfCancellationRequested();
            resultado.Lotes++;
            try
            {
                using var ambito = ambitoFactory.CreateScope();
                creadasEnElTenant += await ambito.ServiceProvider.GetRequiredService<IConciliacionGestionesAutomaticasService>()
                    .ConciliarLoteAsync(lote, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                resultado.LotesFallidos++;
                logger.LogError(ex,
                    "Falló un lote de {Documentos} Documentos de la conciliación de Gestiones automáticas del Tenant propietario {TenantId}; se continúa con el resto.",
                    lote.Length, tenantId);
            }
        }

        resultado.Creadas += creadasEnElTenant;
        if (creadasEnElTenant > 0)
            logger.LogInformation(
                "Conciliación de Gestiones automáticas: {Creadas} creadas en el Tenant propietario {TenantId}.",
                creadasEnElTenant, tenantId);
    }

    /// <summary>Recuento de una pasada. Completa solo si ningún Tenant ni ningún lote falló.</summary>
    internal sealed class ResultadoPasada
    {
        public int Tenants { get; init; }
        public int TenantsFallidos { get; set; }
        public int Lotes { get; set; }
        public int LotesFallidos { get; set; }
        public int Creadas { get; set; }
        public bool Completa => TenantsFallidos == 0 && LotesFallidos == 0;
    }
}
