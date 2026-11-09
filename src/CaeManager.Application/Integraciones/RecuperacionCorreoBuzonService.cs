using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Lectura del hueco de correo de las conexiones de Microsoft 365 que dejaron de estar operativas
/// (<see cref="ConexionIntegracion.HuecoPendienteDesdeUtc"/>): lo que llegó mientras la ingesta descartaba sus
/// notificaciones (ConError) no vuelve a notificarse, y sin esto se perdería. No es un sondeo delta: solo corre sobre
/// las conexiones con la marca, y la rehabilitación no lee nada en línea; deja la marca, y esto la atiende.
/// <list type="bullet">
/// <item>Lo invoca <c>IngestaWebhookHostedService</c> cada <see cref="IntervaloReintento"/>, por Tenant, sobre las
/// conexiones operativas con marca. Un fallo (token, 429, 5xx, tiempo agotado, respuesta ilegible, excepción) deja la
/// marca para el siguiente intento.</item>
/// <item>Lee la carpeta Inbox desde la marca menos <see cref="Margen"/>, nunca desde el último mensaje ingerido (las
/// notificaciones no llegan en orden), nunca antes de <see cref="ConexionIntegracion.FechaConectadaUtc"/> ni más atrás
/// de <see cref="AntiguedadMaxima"/>, y como mucho <see cref="MaximoPaginas"/> páginas de <see cref="TamanoPagina"/>
/// mensajes, de los más recientes a los más antiguos.</item>
/// <item>Antes de cada página relee de la base el estado de la conexión: si ya no está operativa, no lee.</item>
/// <item>Descarta los ya ingeridos con la misma clave que la ingesta
/// (<see cref="IConversacionRepository.ExisteMensajeExternoAsync"/>, el Id inmutable de Graph) y encola el resto como
/// un único <see cref="EventoWebhook"/>, que la ingesta procesa por su camino de siempre: conversación, adjuntos,
/// acuse y sus guardas.</item>
/// <item>Termina completa: limpia la marca en la misma transacción que encola. Llega a un tope: la marca pasa a
/// <see cref="ConexionIntegracion.CorreoNoRecuperadoDesdeUtc"/>, el aviso de Conexiones; nunca se limpia en silencio.</item>
/// </list>
/// </summary>
public class RecuperacionCorreoBuzonService(
    IIntegracionesQueryContext queryContext,
    IConexionIntegracionRepository conexionRepositorio,
    AccesoGraphService accesoGraph,
    IMicrosoft365GraphClient graphClient,
    IConversacionRepository conversacionRepositorio,
    IEventoWebhookRepository eventoRepositorio,
    IUnitOfWork unitOfWork,
    IDescarteCambiosPendientes descarteCambios,
    ILogger<RecuperacionCorreoBuzonService> logger)
{
    public const int TamanoPagina = 50;
    public const int MaximoPaginas = 5;

    /// <summary>Nunca se recupera correo recibido hace más de esto.</summary>
    public static readonly TimeSpan AntiguedadMaxima = TimeSpan.FromDays(7);

    /// <summary>Margen antes de la marca, por el desfase de reloj entre Graph y TALVEG: la deduplicación descarta lo que se solape.</summary>
    public static readonly TimeSpan Margen = TimeSpan.FromMinutes(5);

    /// <summary>Cada cuánto se vuelve a intentar una lectura pendiente: los fallos de Graph suelen ser limitaciones de ritmo.</summary>
    public static readonly TimeSpan IntervaloReintento = TimeSpan.FromMinutes(5);

    /// <summary>Carpeta conocida de Graph: la misma que suscribe la notificación de correo entrante.</summary>
    private const string CarpetaEntrada = "inbox";

    public enum Resultado
    {
        /// <summary>La conexión no existe o no tiene marca.</summary>
        SinHueco,

        /// <summary>La conexión no está operativa (al empezar o al releerla antes de una página): no se lee, la marca se queda.</summary>
        NoOperativa,

        /// <summary>Fallo de Graph o del token: la marca se queda para el siguiente intento.</summary>
        Reintentar,

        /// <summary>Lectura completa: la marca se limpió.</summary>
        Completa,

        /// <summary>Tope de antigüedad o de mensajes: la marca pasó a aviso de correo no recuperado.</summary>
        TopeAlcanzado,
    }

    /// <summary>Atiende cada conexión operativa del Tenant actual con hueco pendiente. Una que falla no para a las siguientes.</summary>
    public async Task RecuperarPendientesAsync(CancellationToken cancellationToken)
    {
        var conexionIds = await queryContext.ConexionesIntegracion
            .Where(c => c.Proveedor == ProveedorIntegracion.Microsoft365 && c.HuecoPendienteDesdeUtc != null)
            .OperativasVerificadas(DateTime.UtcNow)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        foreach (var conexionId in conexionIds)
        {
            try
            {
                await RecuperarAsync(conexionId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Incluye un choque de concurrencia (otro proceso cambió la conexión entre la carga y el guardado): la
                // marca no se limpió y el evento no se guardó; el siguiente intento vuelve a leer.
                logger.LogError(ex, "Falló la lectura del hueco de correo de la conexión {ConexionId}; se reintentará.", conexionId);
                descarteCambios.DescartarCambiosPendientes();
            }
        }
    }

    public async Task<Resultado> RecuperarAsync(Guid conexionId, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
        if (conexion?.HuecoPendienteDesdeUtc is not { } marca)
            return Resultado.SinHueco;

        var ahoraUtc = DateTime.UtcNow;
        if (!conexion.OperativaVerificada(ahoraUtc))
            return Resultado.NoOperativa;

        var limiteAntiguedad = ahoraUtc - AntiguedadMaxima;
        var topeAlcanzado = marca < limiteAntiguedad;
        var desde = marca - Margen;
        if (desde < conexion.FechaConectadaUtc)
            desde = conexion.FechaConectadaUtc;
        if (desde < limiteAntiguedad)
            desde = limiteAntiguedad;

        var token = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion, cancellationToken);
        if (token.EsFallido)
        {
            logger.LogWarning(
                "Sin token para leer el hueco de correo de la conexión {ConexionId}; se reintentará: {Error}", conexionId, token.Error.Mensaje);
            return Resultado.Reintentar;
        }

        // El token puede haberse renovado y Graph invalida el refresh token anterior: se guarda antes de leer, para que
        // un fallo posterior no lo pierda.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var buzon = BuzonGraph.De(conexion);
        var pendientes = new List<string>();
        var vistos = new HashSet<string>(StringComparer.Ordinal);
        var completa = false;

        for (var pagina = 0; pagina < MaximoPaginas && !completa; pagina++)
        {
            // Puerta antes de cada página: otro proceso puede haber pasado la conexión a ConError o desconectado.
            if (!await conexionRepositorio.SigueOperativaVerificadaAsync(conexionId, DateTime.UtcNow, cancellationToken))
            {
                logger.LogWarning(
                    "La conexión {ConexionId} dejó de estar operativa durante la lectura de su hueco de correo: no se lee más.", conexionId);
                return Resultado.NoOperativa;
            }

            var listado = await graphClient.ListarMensajesAsync(
                token.Valor, buzon, CarpetaEntrada, TamanoPagina, pagina * TamanoPagina, cancellationToken);
            if (listado.EsFallido)
            {
                logger.LogWarning(
                    "No se pudo leer la carpeta Inbox de la conexión {ConexionId} para recuperar su correo; se reintentará: {Error}",
                    conexionId, listado.Error.Mensaje);
                return Resultado.Reintentar;
            }

            foreach (var mensaje in listado.Valor)
            {
                if (mensaje.FechaUtc < desde)
                {
                    completa = true;
                    break;
                }

                if (vistos.Add(mensaje.MensajeExternoId)
                    && !await conversacionRepositorio.ExisteMensajeExternoAsync(conexionId, mensaje.MensajeExternoId, cancellationToken))
                    pendientes.Add(mensaje.MensajeExternoId);
            }

            if (listado.Valor.Count < TamanoPagina)
                completa = true;
        }

        // Cinco páginas llenas sin llegar a la fecha de inicio: lo anterior no se lee.
        if (!completa)
            topeAlcanzado = true;

        if (pendientes.Count > 0)
        {
            // Del más antiguo al más reciente, como llegarían por el webhook.
            pendientes.Reverse();
            eventoRepositorio.Agregar(new EventoWebhook(conexionId, graphClient.ConstruirNotificacionDeRecuperacion(pendientes)));
        }

        if (topeAlcanzado)
        {
            conexion.CerrarHuecoConCorreoNoRecuperado(marca);
            logger.LogWarning(
                "La lectura del hueco de la conexión {ConexionId} llegó a su tope ({Maximo} mensajes o {Dias} días): puede faltar correo desde {Marca:O}.",
                conexionId, MaximoPaginas * TamanoPagina, AntiguedadMaxima.TotalDays, marca);
        }
        else
        {
            conexion.CerrarHuecoRecuperado(marca);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Encolados {Cantidad} mensajes sin ingerir de la conexión {ConexionId} desde {Desde:O}.", pendientes.Count, conexionId, desde);
        return topeAlcanzado ? Resultado.TopeAlcanzado : Resultado.Completa;
    }
}
