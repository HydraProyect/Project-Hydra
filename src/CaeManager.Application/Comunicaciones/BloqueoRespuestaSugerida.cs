using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones;

/// <summary>
/// Bloqueo en servidor de la respuesta sugerida: nunca se envía sin que una persona la revise. Lo llaman, antes de tocar
/// el canal, todos los comandos de Comunicaciones que envían texto de una persona. La interfaz bloquea además Enviar y
/// Ctrl+Intro, pero la garantía es esta. Escribir a mano sin insertar la sugerencia no bloquea. Editar el texto por dentro
/// tampoco: cuenta como edición.
/// </summary>
public static class BloqueoRespuestaSugerida
{
    /// <summary>
    /// Por debajo de esta longitud, ya normalizada, una sugerencia solo se compara por igualdad: un texto tan corto puede
    /// aparecer dentro de cualquier mensaje escrito a mano.
    /// </summary>
    public const int LongitudMinimaTextoContenido = 40;

    public static readonly Error SugerenciaSinRevisar = Error.Crear(
        "Respuesta.SugerenciaSinRevisar", "Revisa la respuesta sugerida antes de enviar: confírmala, edítala o descártala.");

    /// <summary>
    /// Falla si la conversación tiene una sugerencia en <see cref="EstadoRespuestaSugerida.SinRevisar"/>, o si
    /// <paramref name="cuerpo"/> coincide con una en <see cref="EstadoRespuestaSugerida.Propuesta"/>, que es la vía de
    /// enviarla sin pasar por Insertar, o con una <see cref="EstadoRespuestaSugerida.Descartada"/> que contestaba al último
    /// entrante: otra persona puede tener ese texto en el editor, y descartarla no lo revisa. Coincide si es el mismo texto
    /// o si el cuerpo lo contiene entero, para que añadir una firma o un saludo no eluda el bloqueo; desde
    /// <see cref="LongitudMinimaTextoContenido"/>. Una coincidencia deja de bloquear si una persona confirmó ese mismo texto
    /// para ese mismo mensaje.
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
            .Select(r => new SugerenciaComparable(r.Estado, r.MensajeOrigenId, r.TextoHash, r.Texto))
            .ToListAsync(cancellationToken);

        if (sugerencias.Any(r => r.Estado == EstadoRespuestaSugerida.SinRevisar))
            return Result.Fallo(SugerenciaSinRevisar);

        var hash = RespuestaSugerida.CalcularTextoHash(cuerpo);
        var cuerpoNormalizado = RespuestaSugerida.NormalizarTexto(cuerpo);

        return HayCoincidenciaSinRevisar(sugerencias, r => r.TextoHash == hash || ContieneElTexto(cuerpoNormalizado, r.Texto!))
            ? Result.Fallo(SugerenciaSinRevisar)
            : Result.Exito();
    }

    /// <summary>
    /// Para un mensaje nuevo: el proveedor decide después de enviarlo si cae en un hilo ya existente, y solo puede ser un
    /// hilo de este buzón. Antes de enviar no se sabe cuál, así que se aplican a los hilos del buzón las mismas reglas que a
    /// una conversación, pero en SQL y solo por hash: un texto contenido no se detecta aquí, porque exigiría descargar los
    /// textos de todo el buzón. Solo cuentan los hilos que quien envía puede ver: un bloqueo sobre otro delataría que ese
    /// texto existe.
    /// </summary>
    public static async Task<Result> ComprobarEnvioPorBuzonAsync(
        this IComunicacionesQueryContext comunicaciones, IAlcanceDatosService alcanceDatos, Guid conexionIntegracionId, string cuerpo,
        CancellationToken cancellationToken)
    {
        var hash = RespuestaSugerida.CalcularTextoHash(cuerpo);
        var coincidencias = await (
            from respuesta in comunicaciones.RespuestasSugeridas
            where respuesta.TextoHash == hash
            join conversacion in comunicaciones.Conversaciones on respuesta.ConversacionId equals conversacion.Id
            where conversacion.ConexionIntegracionId == conexionIntegracionId
            let ultimoEntrante = comunicaciones.Mensajes
                .Where(m => m.ConversacionId == conversacion.Id && m.Direccion == DireccionMensaje.Entrante && !m.EsAutomatico)
                .OrderByDescending(m => m.FechaUtc)
                .Select(m => (Guid?)m.Id)
                .FirstOrDefault()
            where respuesta.Estado == EstadoRespuestaSugerida.Propuesta || respuesta.Estado == EstadoRespuestaSugerida.SinRevisar ||
                  respuesta.MensajeOrigenId == ultimoEntrante
            select new { Conversacion = conversacion, respuesta.Estado, respuesta.MensajeOrigenId, respuesta.TextoHash })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (var hilo in coincidencias.GroupBy(c => c.Conversacion.Id))
        {
            var sugerencias = hilo.Select(c => new SugerenciaComparable(c.Estado, c.MensajeOrigenId, c.TextoHash)).ToList();
            var bloquea = sugerencias.Any(r => r.Estado == EstadoRespuestaSugerida.SinRevisar) ||
                          HayCoincidenciaSinRevisar(sugerencias, _ => true);

            if (bloquea && await alcanceDatos.ConversacionVisibleAsync(hilo.First().Conversacion, cancellationToken))
                return Result.Fallo(SugerenciaSinRevisar);
        }

        return Result.Exito();
    }

    private static bool HayCoincidenciaSinRevisar(
        IReadOnlyCollection<SugerenciaComparable> sugerencias, Func<SugerenciaComparable, bool> coincide) =>
        sugerencias.Any(p =>
            p.Estado is EstadoRespuestaSugerida.Propuesta or EstadoRespuestaSugerida.Descartada &&
            coincide(p) &&
            !sugerencias.Any(r => r.Estado == EstadoRespuestaSugerida.Revisada &&
                                  r.TextoHash == p.TextoHash && r.MensajeOrigenId == p.MensajeOrigenId));

    private static bool ContieneElTexto(string cuerpoNormalizado, string texto)
    {
        var textoNormalizado = RespuestaSugerida.NormalizarTexto(texto);
        return textoNormalizado.Length >= LongitudMinimaTextoContenido &&
               cuerpoNormalizado.Contains(textoNormalizado, StringComparison.Ordinal);
    }

    private sealed record SugerenciaComparable(
        EstadoRespuestaSugerida Estado, Guid MensajeOrigenId, string TextoHash, string? Texto = null);
}
