using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones;

/// <summary>
/// Bloqueo en servidor de la respuesta sugerida: nunca se envía sin que una persona la revise. Lo llaman, antes de tocar
/// el canal, todos los comandos de Comunicaciones que envían texto de una persona. La interfaz bloquea además Enviar y
/// Ctrl+Intro, pero la garantía es esta. Escribir a mano sin insertar la sugerencia no bloquea. Compara por
/// <see cref="RespuestaSugerida.TextoHash"/>: el contenido del borrador no hace falta para decidir.
/// </summary>
public static class BloqueoRespuestaSugerida
{
    public static readonly Error SugerenciaSinRevisar = Error.Crear(
        "Respuesta.SugerenciaSinRevisar", "Revisa la respuesta sugerida antes de enviar: confírmala, edítala o descártala.");

    /// <summary>
    /// Falla si la conversación tiene una sugerencia en <see cref="EstadoRespuestaSugerida.SinRevisar"/>, o si
    /// <paramref name="cuerpo"/> coincide con una en <see cref="EstadoRespuestaSugerida.Propuesta"/>, que es la vía de
    /// enviarla sin pasar por Insertar, o con una <see cref="EstadoRespuestaSugerida.Descartada"/> que contestaba al último
    /// entrante: otra persona puede tener ese texto en el editor, y descartarla no lo revisa. Una coincidencia deja de
    /// bloquear si una persona confirmó ese mismo texto para ese mismo mensaje.
    /// </summary>
    public static async Task<Result> ComprobarEnvioAsync(
        this IComunicacionesQueryContext comunicaciones, Guid conversacionId, string cuerpo, CancellationToken cancellationToken)
    {
        var ultimoEntrante = await comunicaciones.Mensajes
            .Where(m => m.ConversacionId == conversacionId && m.Direccion == DireccionMensaje.Entrante && !m.EsAutomatico)
            .OrderByDescending(m => m.FechaUtc)
            .Select(m => (Guid?)m.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var sugerencias = await comunicaciones.RespuestasSugeridas
            .Where(r => r.ConversacionId == conversacionId &&
                        (r.Estado == EstadoRespuestaSugerida.Propuesta || r.Estado == EstadoRespuestaSugerida.SinRevisar ||
                         r.MensajeOrigenId == ultimoEntrante))
            .Select(r => new { r.Estado, r.TextoHash, r.MensajeOrigenId })
            .ToListAsync(cancellationToken);

        if (sugerencias.Any(r => r.Estado == EstadoRespuestaSugerida.SinRevisar))
            return Result.Fallo(SugerenciaSinRevisar);

        var hash = RespuestaSugerida.CalcularTextoHash(cuerpo);
        var pendientes = sugerencias.Where(r =>
            r.TextoHash == hash && r.Estado is EstadoRespuestaSugerida.Propuesta or EstadoRespuestaSugerida.Descartada);
        var revisadas = sugerencias.Where(r => r.TextoHash == hash && r.Estado == EstadoRespuestaSugerida.Revisada);

        return pendientes.Any(p => revisadas.All(r => r.MensajeOrigenId != p.MensajeOrigenId))
            ? Result.Fallo(SugerenciaSinRevisar)
            : Result.Exito();
    }

    /// <summary>
    /// Para un mensaje nuevo: el proveedor decide después de enviarlo si cae en un hilo ya existente, y solo puede ser un
    /// hilo de este buzón. Antes de enviar no se sabe cuál, así que se compara con las sugerencias activas del buzón, en SQL
    /// y por hash. Solo cuentan los hilos que quien envía puede ver: un bloqueo sobre otro delataría que ese texto existe.
    /// </summary>
    public static async Task<Result> ComprobarEnvioPorBuzonAsync(
        this IComunicacionesQueryContext comunicaciones, IAlcanceDatosService alcanceDatos, Guid conexionIntegracionId, string cuerpo,
        CancellationToken cancellationToken)
    {
        var hash = RespuestaSugerida.CalcularTextoHash(cuerpo);
        var hilos = await (
            from respuesta in comunicaciones.RespuestasSugeridas
            where respuesta.TextoHash == hash &&
                  (respuesta.Estado == EstadoRespuestaSugerida.Propuesta || respuesta.Estado == EstadoRespuestaSugerida.SinRevisar)
            join conversacion in comunicaciones.Conversaciones on respuesta.ConversacionId equals conversacion.Id
            where conversacion.ConexionIntegracionId == conexionIntegracionId
            select conversacion)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (var hilo in hilos)
        {
            if (await alcanceDatos.ConversacionVisibleAsync(hilo, cancellationToken))
                return Result.Fallo(SugerenciaSinRevisar);
        }

        return Result.Exito();
    }
}
