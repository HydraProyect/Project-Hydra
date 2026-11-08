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
/// los Tenants, en pasadas repetidas desde el arranque. Solo se registra si
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
/// rastreado, y el siguiente lote empieza limpio. Igual con un Tenant cuya búsqueda falla. Un lote que no se confirma se
/// reintenta Documento a Documento, cada uno en su ámbito: un Documento que falla siempre no impide guardar a sus
/// acompañantes, y queda en el registro con su Id y su Tenant propietario en cada pasada. La pasada cuenta Tenants y
/// Documentos fallidos (<see cref="ResultadoPasada"/>) y solo es completa sin ninguno.
/// </para>
///
/// <para>
/// <b>Pasadas repetidas, no una espera fija.</b> La primera pasada empieza al arrancar y se repite cada
/// <see cref="ConciliacionGestionesAutomaticasOptions.IntervaloPasadas"/> durante
/// <see cref="ConciliacionGestionesAutomaticasOptions.DuracionPasadas"/>, aunque una pasada salga completa: el código no
/// sabe cuándo termina el drenaje de la ranura anterior, y lo que esta confirme después de una pasada lo recoge la
/// siguiente. La ventana es fija (<see cref="ConciliacionGestionesAutomaticasOptions.DesdeUtc"/>), no relativa al
/// arranque: un reinicio posterior con la opción encendida vuelve a mirar todo el relevo. Cada pasada pide el liderazgo:
/// con varias instancias, solo una concilia a la vez; la que no lo obtiene espera a la pasada siguiente, y la pasada que
/// repite lo ya conciliado no crea nada.
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
        await EjecutarPasadasAsync(stoppingToken);
    }

    /// <summary>
    /// El bucle de pasadas. Interno para que un test lo ejercite sin el host. Devuelve las pasadas que hizo esta
    /// instancia con el liderazgo, completas o no.
    /// </summary>
    internal async Task<IReadOnlyList<ResultadoPasada>> EjecutarPasadasAsync(CancellationToken stoppingToken)
    {
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();
        var pasadas = new List<ResultadoPasada>();

        try
        {
            // Dentro del try: una sección mal escrita (un TimeSpan ilegible) o una duración que desborda la fecha se
            // registran en vez de dejar fallida la tarea del servicio, que pararía el host.
            var config = opciones.Value;
            if (config.LeerDesdeUtc() is not { } desdeUtc)
            {
                logger.LogError(
                    "La conciliación de Gestiones automáticas no corre: falta {Opcion} en UTC (ISO 8601 terminado en Z), el inicio del despliegue que introduce la función.",
                    $"{ConciliacionGestionesAutomaticasOptions.SeccionConfiguracion}:{nameof(ConciliacionGestionesAutomaticasOptions.DesdeUtc)}");
                return pasadas;
            }
            if (config.IntervaloPasadas <= TimeSpan.Zero)
            {
                logger.LogError("La conciliación de Gestiones automáticas no corre: el intervalo entre pasadas debe ser positivo.");
                return pasadas;
            }

            var finUtc = reloj.GetUtcNow() + config.DuracionPasadas;

            for (var intento = 1; ; intento++)
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

                if (resultado is not null)
                {
                    pasadas.Add(resultado);
                    if (resultado.Completa)
                        logger.LogInformation(
                            "Pasada {Intento} de la conciliación de Gestiones automáticas completa: {Creadas} creadas en {Tenants} Tenants y {Lotes} lotes.",
                            intento, resultado.Creadas, resultado.Tenants, resultado.Lotes);
                    else
                        logger.LogWarning(
                            "Pasada {Intento} de la conciliación de Gestiones automáticas incompleta: {Creadas} creadas, {TenantsFallidos} de {Tenants} Tenants y {DocumentosFallidos} Documentos fallidos; la pasada siguiente lo reintenta.",
                            intento, resultado.Creadas, resultado.TenantsFallidos, resultado.Tenants, resultado.DocumentosFallidos);
                }
                else if (!lider)
                    logger.LogInformation(
                        "Conciliación de Gestiones automáticas: otra instancia tiene la pasada {Intento}; esta espera a la siguiente.", intento);

                if (reloj.GetUtcNow() >= finUtc)
                    break;
                await Task.Delay(config.IntervaloPasadas, reloj, stoppingToken);
            }

            var ultima = pasadas.LastOrDefault();
            if (ultima is null)
                logger.LogWarning(
                    "Conciliación de Gestiones automáticas terminada sin ninguna pasada en esta instancia: otra instancia tuvo el liderazgo o falló cada intento.");
            else if (ultima.Completa)
                logger.LogInformation(
                    "Conciliación de Gestiones automáticas terminada: {Pasadas} pasadas de esta instancia, la última completa.", pasadas.Count);
            else
                logger.LogError(
                    "Conciliación de Gestiones automáticas terminada sin una última pasada completa en esta instancia ({Pasadas} pasadas): revisar los errores anteriores.",
                    pasadas.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Nunca tumba el host: una configuración inválida (ilegible o una duración que desborda la fecha) o un fallo
            // inesperado se registran.
            logger.LogError(ex, "La conciliación de Gestiones automáticas se detuvo por un error.");
        }

        return pasadas;
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
            var (creadas, fallo) = await IntentarLoteAsync(lote, stoppingToken);
            if (fallo is null)
            {
                creadasEnElTenant += creadas;
                continue;
            }

            if (lote.Length == 1)
            {
                RegistrarDocumentoFallido(resultado, fallo, lote[0], tenantId);
                continue;
            }

            // El lote no se confirmó. Uno a uno, cada Documento en su ámbito: el que falla siempre queda aislado y en el
            // registro, y sus acompañantes se guardan.
            resultado.LotesDivididos++;
            logger.LogWarning(fallo,
                "Falló un lote de {Documentos} Documentos de la conciliación de Gestiones automáticas del Tenant propietario {TenantId}; se reintenta Documento a Documento.",
                lote.Length, tenantId);
            foreach (var documentoId in lote)
            {
                stoppingToken.ThrowIfCancellationRequested();
                var (delDocumento, falloDelDocumento) = await IntentarLoteAsync([documentoId], stoppingToken);
                if (falloDelDocumento is null)
                    creadasEnElTenant += delDocumento;
                else
                    RegistrarDocumentoFallido(resultado, falloDelDocumento, documentoId, tenantId);
            }
        }

        resultado.Creadas += creadasEnElTenant;
        if (creadasEnElTenant > 0)
            logger.LogInformation(
                "Conciliación de Gestiones automáticas: {Creadas} creadas en el Tenant propietario {TenantId}.",
                creadasEnElTenant, tenantId);
    }

    /// <summary>Un lote en su propio ámbito de servicios: las creadas, o el fallo si no se confirmó.</summary>
    private async Task<(int Creadas, Exception? Fallo)> IntentarLoteAsync(Guid[] lote, CancellationToken stoppingToken)
    {
        try
        {
            using var ambito = ambitoFactory.CreateScope();
            return (await ambito.ServiceProvider.GetRequiredService<IConciliacionGestionesAutomaticasService>()
                .ConciliarLoteAsync(lote, stoppingToken), null);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (0, ex);
        }
    }

    private void RegistrarDocumentoFallido(ResultadoPasada resultado, Exception fallo, Guid documentoId, Guid tenantId)
    {
        resultado.DocumentosFallidos++;
        logger.LogError(fallo,
            "La conciliación de Gestiones automáticas no pudo guardar la Gestión del Documento {DocumentoId} del Tenant propietario {TenantId}; la pasada siguiente lo reintenta.",
            documentoId, tenantId);
    }

    /// <summary>Recuento de una pasada. Completa solo si ningún Tenant ni ningún Documento falló.</summary>
    internal sealed class ResultadoPasada
    {
        public int Tenants { get; init; }
        public int TenantsFallidos { get; set; }
        public int Lotes { get; set; }

        /// <summary>Lotes que no se confirmaron enteros y se reintentaron Documento a Documento.</summary>
        public int LotesDivididos { get; set; }

        public int DocumentosFallidos { get; set; }
        public int Creadas { get; set; }
        public bool Completa => TenantsFallidos == 0 && DocumentosFallidos == 0;
    }
}
