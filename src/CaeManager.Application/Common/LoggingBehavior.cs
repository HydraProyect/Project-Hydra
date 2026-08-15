using System.Diagnostics;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Common;

/// <summary>
/// Traza cada request de MediatR: qué se ejecutó, cuánto tardó, con qué
/// tenant y usuario, y cómo terminó (P1-10 de
/// docs/business/MATURITY_REVIEW.md — antes no había un solo <c>ILogger</c>
/// en los 73 Command handlers ni en los 76 Query handlers, así que la
/// pregunta "¿qué comandos fallan y con qué latencia?" no tenía respuesta).
///
/// <b>Nunca registra el contenido del request.</b> Los Commands llevan DNI,
/// nombres, correos y datos de salud de trabajadores; volcarlos al log los
/// convertiría en un tratamiento de datos personales más, con su propia
/// retención y sus propios derechos de acceso (RGPD-TRATAMIENTO-DATOS.md).
/// Se registra el <i>tipo</i> del request, no sus valores.
///
/// Va el primero del pipeline —por fuera incluso de
/// <c>SerializacionAccesoDatosBehavior</c>— para medir lo que el usuario
/// espera de verdad, incluida la cola de acceso a datos. Puede hacerlo sin
/// riesgo porque no toca la base de datos: <c>ITenantActual</c> es una
/// propiedad síncrona ya resuelta y las dos llamadas a
/// <c>ICurrentUserService</c> que usa leen claims, no filas.
///
/// Los nombres de propiedad (<c>Request</c>, <c>TenantId</c>,
/// <c>UsuarioId</c>, <c>DuracionMs</c>) se eligen estables a propósito: son
/// los mismos que reutiliza OpenTelemetry ahora que se añadió (Horizonte 2.3,
/// antes P3-30) como tags del span de <see cref="Observabilidad.ActivitySource"/>
/// más abajo, para no tener que reescribir dashboards ni alertas al migrar.
/// El histograma de latencia (<see cref="Observabilidad.DuracionComandoMs"/>)
/// es la misma medición que este behavior ya hacía, expuesta también como
/// métrica en vez de solo como línea de log.
///
/// Desde Horizonte 2.4, cada desenlace también alimenta
/// <see cref="VentanaSaludOperativa"/> (tasa de error y latencia degradada,
/// las dos alertas por tasa del plan) — reutiliza exactamente la misma
/// clasificación fallo/lento que ya se calculaba aquí para el log, sin
/// duplicar lógica.
/// </summary>
public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>
    /// Por encima de esto, el request se registra como aviso aunque haya ido
    /// bien. Un segundo es mucho para Blazor Server: cada Query bloquea el
    /// renderizado del componente que la lanzó.
    /// </summary>
    private const int UmbralLentitudMs = 1000;

    // Categoría fija en vez de ILogger<LoggingBehavior<TRequest,TResponse>>:
    // esa produce un nombre distinto por cada combinación de tipos, y los
    // niveles de Serilog se configuran por categoría (appsettings.json,
    // sección Serilog:MinimumLevel:Override). Con una sola categoría, subir o
    // bajar el detalle de toda la traza de aplicación es una línea.
    public const string CategoriaLog = "CaeManager.Application.Requests";

    private readonly ILogger _logger;
    private readonly ICurrentUserService _currentUserService;
    private readonly ITenantActual _tenantActual;
    private readonly VentanaSaludOperativa _ventanaSalud;

    public LoggingBehavior(
        ILoggerFactory loggerFactory, ICurrentUserService currentUserService, ITenantActual tenantActual,
        VentanaSaludOperativa ventanaSalud)
    {
        _logger = loggerFactory.CreateLogger(CategoriaLog);
        _currentUserService = currentUserService;
        _tenantActual = tenantActual;
        _ventanaSalud = ventanaSalud;
    }

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var nombre = typeof(TRequest).Name;
        var esEscritura = request is ICommandBase;

        var ambito = await ConstruirAmbitoAsync();
        using var ambitoLog = _logger.BeginScope(ambito);

        // Un span por request de MediatR, con los mismos nombres de
        // propiedad que el ámbito de log de arriba (ver el comentario de
        // clase) — si no hay ningún listener suscrito a este
        // ActivitySource (Program.cs, "Serilog:Seq:ServerUrl" sin
        // configurar), StartActivity devuelve null y las llamadas ?. de
        // abajo son prácticamente gratis.
        using var actividad = Observabilidad.ActivitySource.StartActivity(nombre, ActivityKind.Internal);
        actividad?.SetTag("Request", nombre);
        // ToString() explícito: los valores del ámbito son Guid?/objetos, y
        // el exportador OTLP solo tiene garantizado un tipo primitivo por
        // atributo — igual que ya se ve el TenantId en Seq a través del log
        // correlacionado.
        if (ambito.TryGetValue("TenantId", out var tenantIdTag))
            actividad?.SetTag("TenantId", tenantIdTag?.ToString());
        if (ambito.TryGetValue("UsuarioId", out var usuarioIdTag))
            actividad?.SetTag("UsuarioId", usuarioIdTag?.ToString());

        var cronometro = Stopwatch.StartNew();

        try
        {
            var respuesta = await next(cancellationToken);
            cronometro.Stop();

            RegistrarDesenlace(nombre, esEscritura, cronometro.ElapsedMilliseconds, respuesta, actividad);

            return respuesta;
        }
        catch (OperationCanceledException)
        {
            // Un circuito de Blazor cancela requests constantemente (el
            // usuario navega, un debounce se reemplaza). No es un fallo.
            cronometro.Stop();
            actividad?.SetTag("DuracionMs", cronometro.ElapsedMilliseconds);
            _logger.LogDebug(
                "{Request} cancelado tras {DuracionMs} ms", nombre, cronometro.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            cronometro.Stop();
            // Lo verdaderamente inesperado: aquí sí interesa la excepción
            // completa, porque no hay Result que la describa (ver
            // ARCHITECTURE.md, "Manejo de errores"). El span lo refleja con
            // el mismo criterio: Error, no un Result de negocio.
            actividad?.SetTag("DuracionMs", cronometro.ElapsedMilliseconds);
            actividad?.SetTag("exception.type", ex.GetType().FullName);
            actividad?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(
                ex, "{Request} lanzó {Excepcion} tras {DuracionMs} ms",
                nombre, ex.GetType().Name, cronometro.ElapsedMilliseconds);
            // Alertas por tasa (Horizonte 2.4): una excepción es un fallo
            // igual que un Result fallido a efectos de la tasa de error —
            // ver RegistrarDesenlace para el otro punto de entrada.
            _ventanaSalud.RegistrarDesenlace(fallo: true, lento: cronometro.ElapsedMilliseconds >= UmbralLentitudMs);
            throw;
        }
    }

    private void RegistrarDesenlace(string nombre, bool esEscritura, long duracionMs, TResponse respuesta, Activity? actividad)
    {
        actividad?.SetTag("DuracionMs", duracionMs);
        // Sin TenantId/UsuarioId a propósito (ver Observabilidad): esos van
        // en el span, no en la métrica, para no disparar la cardinalidad de
        // las series temporales de un histograma que sí se consulta agregado
        // (p95 por comando).
        Observabilidad.DuracionComandoMs.Record(duracionMs, new KeyValuePair<string, object?>("Request", nombre));

        // Alertas por tasa (Horizonte 2.4): se alimenta aquí, antes de
        // cualquier "return" de abajo, para que todo desenlace (éxito,
        // Result fallido, lento) cuente exactamente una vez en la ventana.
        _ventanaSalud.RegistrarDesenlace(
            fallo: respuesta is Result { EsFallido: true }, lento: duracionMs >= UmbralLentitudMs);

        // Un Result fallido es un desenlace de negocio esperado, no un error
        // del sistema: se registra como aviso y con su código, que es lo que
        // permite contar "cuántas veces se deniega por rol" o "cuántos
        // choques de concurrencia hay" sin instrumentar nada más.
        if (respuesta is Result { EsFallido: true } resultado)
        {
            actividad?.SetTag("CodigoError", resultado.Error.Codigo);
            actividad?.SetStatus(ActivityStatusCode.Error, resultado.Error.Codigo);
            _logger.LogWarning(
                "{Request} falló con {CodigoError} en {DuracionMs} ms",
                nombre, resultado.Error.Codigo, duracionMs);
            return;
        }

        actividad?.SetStatus(ActivityStatusCode.Ok);

        if (duracionMs >= UmbralLentitudMs)
        {
            _logger.LogWarning(
                "{Request} completado en {DuracionMs} ms (por encima del umbral de {UmbralMs} ms)",
                nombre, duracionMs, UmbralLentitudMs);
            return;
        }

        // Las escrituras se registran siempre: son pocas, cambian datos y son
        // lo que se quiere poder reconstruir después. Las lecturas, no: en
        // Blazor Server cada tecleo del buscador es una Query, y registrarlas
        // todas a Information ahogaría el log con ruido.
        if (esEscritura)
            _logger.LogInformation("{Request} completado en {DuracionMs} ms", nombre, duracionMs);
        else
            _logger.LogDebug("{Request} completado en {DuracionMs} ms", nombre, duracionMs);
    }

    /// <summary>
    /// Serilog convierte el estado de <c>BeginScope</c> en propiedades del
    /// evento, así que todo lo que se registre dentro del request —incluido
    /// lo que registren los handlers o EF Core— sale ya correlacionado con
    /// tenant y usuario, sin que nadie tenga que acordarse de pasarlos.
    /// </summary>
    private async Task<Dictionary<string, object?>> ConstruirAmbitoAsync()
    {
        var tenantId = _tenantActual.TenantId;
        var tenantOrigenId = await _currentUserService.ObtenerTenantOrigenIdAsync();

        var ambito = new Dictionary<string, object?>
        {
            ["TenantId"] = tenantId,
            ["UsuarioId"] = await _currentUserService.ObtenerUsuarioActualIdAsync(),
        };

        // Solo cuando difieren, que es exactamente la señal de que se está
        // operando un Delegated Workspace ajeno (ADR-004 § 6) — el caso que
        // más falta hace poder reconstruir a posteriori.
        if (tenantOrigenId is not null && tenantOrigenId != tenantId)
            ambito["TenantOrigenId"] = tenantOrigenId;

        return ambito;
    }
}
