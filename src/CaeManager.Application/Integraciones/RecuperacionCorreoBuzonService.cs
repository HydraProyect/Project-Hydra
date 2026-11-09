using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Lectura puntual y acotada de la carpeta Inbox de un buzón de Microsoft 365 cuando su conexión vuelve a estar
/// verificada (o se rehabilita) tras no estarlo: lo que llegó mientras la ingesta descartaba sus notificaciones
/// (ConError) no vuelve a notificarse, y sin esto se perdería. No es un sondeo delta: corre solo en esa transición.
/// <list type="bullet">
/// <item>Lee desde el último mensaje entrante ya ingerido por la conexión (o desde que se conectó), nunca más atrás
/// de <see cref="AntiguedadMaxima"/>, y como mucho <see cref="MaximoPaginas"/> páginas de
/// <see cref="TamanoPagina"/> mensajes, de los más recientes a los más antiguos.</item>
/// <item>Descarta los que ya están ingeridos (<see cref="IConversacionRepository.ExisteMensajeExternoAsync"/>, por el
/// Id inmutable de Graph, el mismo que trae la notificación) y encola el resto como un único
/// <see cref="EventoWebhook"/>, que la ingesta procesa como una notificación más: con sus reintentos, su
/// deduplicación y sus acuses.</item>
/// <item>Es de mejor esfuerzo: un fallo de Graph se registra y no impide la verificación ni la rehabilitación.</item>
/// </list>
/// </summary>
public class RecuperacionCorreoBuzonService(
    IMicrosoft365GraphClient graphClient,
    IConversacionRepository conversacionRepositorio,
    IEventoWebhookRepository eventoRepositorio,
    ILogger<RecuperacionCorreoBuzonService> logger)
{
    public const int TamanoPagina = 50;
    public const int MaximoPaginas = 5;

    /// <summary>Nunca se recupera correo recibido hace más de esto, aunque el último ingerido sea anterior.</summary>
    public static readonly TimeSpan AntiguedadMaxima = TimeSpan.FromDays(7);

    /// <summary>Margen sobre la fecha del último ingerido: la deduplicación descarta lo que se solape.</summary>
    private static readonly TimeSpan Solape = TimeSpan.FromMinutes(5);

    /// <summary>Carpeta conocida de Graph: la misma que suscribe la notificación de correo entrante.</summary>
    private const string CarpetaEntrada = "inbox";

    /// <returns>Cuántos mensajes se encolaron; el evento queda añadido al repositorio y lo guarda el llamador.</returns>
    public async Task<int> EncolarHuecoAsync(
        ConexionIntegracion conexion, string accessToken, DateTime ahoraUtc, CancellationToken cancellationToken)
    {
        try
        {
            var ultimoIngerido = await conversacionRepositorio.ObtenerFechaUltimoMensajeEntranteAsync(conexion.Id, cancellationToken);
            var desde = Max(ultimoIngerido is { } fecha ? fecha - Solape : conexion.FechaConectadaUtc, ahoraUtc - AntiguedadMaxima);
            if (desde < conexion.FechaConectadaUtc)
                desde = conexion.FechaConectadaUtc;

            var buzon = BuzonGraph.De(conexion);
            var pendientes = new List<string>();
            var vistos = new HashSet<string>(StringComparer.Ordinal);

            for (var pagina = 0; pagina < MaximoPaginas; pagina++)
            {
                var listado = await graphClient.ListarMensajesAsync(
                    accessToken, buzon, CarpetaEntrada, TamanoPagina, pagina * TamanoPagina, cancellationToken);
                if (listado.EsFallido)
                {
                    logger.LogWarning(
                        "No se pudo leer la carpeta Inbox de la conexión {ConexionId} para recuperar su correo: {Error}",
                        conexion.Id, listado.Error.Mensaje);
                    break;
                }

                var alcanzadoElLimite = false;
                foreach (var mensaje in listado.Valor)
                {
                    if (mensaje.FechaUtc < desde)
                    {
                        alcanzadoElLimite = true;
                        break;
                    }

                    if (vistos.Add(mensaje.MensajeExternoId)
                        && !await conversacionRepositorio.ExisteMensajeExternoAsync(conexion.Id, mensaje.MensajeExternoId, cancellationToken))
                        pendientes.Add(mensaje.MensajeExternoId);
                }

                if (alcanzadoElLimite || listado.Valor.Count < TamanoPagina)
                    break;

                if (pagina == MaximoPaginas - 1)
                    logger.LogWarning(
                        "La recuperación de la conexión {ConexionId} llegó al tope de {Maximo} mensajes: lo anterior no se recupera.",
                        conexion.Id, MaximoPaginas * TamanoPagina);
            }

            if (pendientes.Count == 0)
                return 0;

            // Del más antiguo al más reciente, como llegarían por el webhook.
            pendientes.Reverse();
            eventoRepositorio.Agregar(new EventoWebhook(conexion.Id, graphClient.ConstruirNotificacionDeRecuperacion(pendientes)));
            logger.LogInformation(
                "Encolados {Cantidad} mensajes sin ingerir de la conexión {ConexionId} desde {Desde:O}.", pendientes.Count, conexion.Id, desde);
            return pendientes.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Falló la recuperación del correo de la conexión {ConexionId}.", conexion.Id);
            return 0;
        }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
