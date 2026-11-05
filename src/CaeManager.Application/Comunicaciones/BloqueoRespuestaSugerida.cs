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
    public const int LongitudMinimaTextoContenido = 12;

    /// <summary>
    /// Tope de sugerencias candidatas que <see cref="ComprobarEnvioPorBuzonAsync"/> acepta comparar. Las activas son como
    /// mucho una por hilo (índice único parcial) y las demás solo cuentan si contestan al último entrante, así que el
    /// número crece con los hilos de un buzón que alguien dejó con una sugerencia a medias, no con su historial: 500 está
    /// muy por encima de un buzón de piloto. La consulta que lo aplica no trae el texto (unos cientos de bytes por fila).
    /// </summary>
    public const int TopeSugerenciasPorBuzon = 500;

    /// <summary>
    /// Sugerencias cuyos textos se tienen a la vez en memoria al comparar por buzón: un texto admite hasta 1 MiB, y sin lote
    /// el peor caso del tope sería de cientos de MiB.
    /// </summary>
    private const int LoteTextosPorBuzon = 50;

    public static readonly Error SugerenciaSinRevisar = Error.Crear(
        "Respuesta.SugerenciaSinRevisar", "Revisa la respuesta sugerida antes de enviar: confírmala, edítala o descártala.");

    public static readonly Error DemasiadasSugerenciasEnElBuzon = Error.Crear(
        "Respuesta.DemasiadasSugerenciasEnElBuzon",
        "Este buzón tiene demasiadas respuestas sugeridas pendientes para comprobar el envío. Descarta las que ya no uses e inténtalo de nuevo.");

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
    /// una conversación y la misma coincidencia que en <see cref="ComprobarEnvioAsync"/>: el mismo texto o un cuerpo que lo
    /// contiene entero (misma normalización, mismo umbral <see cref="LongitudMinimaTextoContenido"/>), de modo que añadir una
    /// firma o un saludo tampoco eluda el bloqueo. Una diferencia con la conversación: allí cualquier sugerencia
    /// <see cref="EstadoRespuestaSugerida.SinRevisar"/> bloquea el hilo entero; aquí solo bloquea la que coincide con el
    /// cuerpo, porque no se sabe en qué hilo caerá y una sola sin revisar frenaría todo el buzón.
    ///
    /// <para>
    /// Cómo: una consulta ligera, sin el texto, trae las sugerencias candidatas de los hilos del buzón (las del
    /// <see cref="EstadoRespuestaSugerida.Propuesta"/> y <see cref="EstadoRespuestaSugerida.SinRevisar"/> de cualquier edad,
    /// más las que contestan al último entrante de su hilo, que es lo que aplica una conversación). Los textos de las que
    /// no están revisadas se descargan por lotes de <see cref="LoteTextosPorBuzon"/> y se descartan tras compararlos, para que la
    /// memoria no crezca con el buzón. Si hay más de <see cref="TopeSugerenciasPorBuzon"/> candidatas no se compara a medias:
    /// falla en cerrado con <see cref="DemasiadasSugerenciasEnElBuzon"/>.
    /// </para>
    ///
    /// <para>
    /// Solo cuentan los hilos que quien envía puede ver: un bloqueo sobre otro delataría que ese texto existe. Una
    /// sugerencia de un hilo no visible no bloquea ni desbloquea. El tope sí se aplica antes de mirar la visibilidad, por lo
    /// que lo puede alcanzar un buzón con muchos hilos ajenos; el error no revela ningún texto ni hilo.
    /// </para>
    /// </summary>
    public static async Task<Result> ComprobarEnvioPorBuzonAsync(
        this IComunicacionesQueryContext comunicaciones, IAlcanceDatosService alcanceDatos, Guid conexionIntegracionId, string cuerpo,
        CancellationToken cancellationToken)
    {
        var candidatas = await (
            from respuesta in comunicaciones.RespuestasSugeridas
            join conversacion in comunicaciones.Conversaciones on respuesta.ConversacionId equals conversacion.Id
            where conversacion.ConexionIntegracionId == conexionIntegracionId
            let ultimoEntrante = comunicaciones.Mensajes
                .Where(m => m.ConversacionId == conversacion.Id && m.Direccion == DireccionMensaje.Entrante && !m.EsAutomatico)
                .OrderByDescending(m => m.FechaUtc)
                .Select(m => (Guid?)m.Id)
                .FirstOrDefault()
            where respuesta.Estado == EstadoRespuestaSugerida.Propuesta || respuesta.Estado == EstadoRespuestaSugerida.SinRevisar ||
                  respuesta.MensajeOrigenId == ultimoEntrante
            orderby respuesta.Id
            select new CandidataDeBuzon(
                respuesta.Id, conversacion.Id, respuesta.Estado, respuesta.MensajeOrigenId, respuesta.TextoHash))
            .Take(TopeSugerenciasPorBuzon + 1)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (candidatas.Count > TopeSugerenciasPorBuzon)
            return Result.Fallo(DemasiadasSugerenciasEnElBuzon);

        var hash = RespuestaSugerida.CalcularTextoHash(cuerpo);
        var coinciden = candidatas.Where(c => c.TextoHash == hash).Select(c => c.Id).ToHashSet();
        var cuerpoNormalizado = RespuestaSugerida.NormalizarTexto(cuerpo);

        // Una Revisada solo desbloquea a las demás: su texto no hace falta para decidir si coincide.
        var porComparar = candidatas.Where(c => c.Estado != EstadoRespuestaSugerida.Revisada && !coinciden.Contains(c.Id)).ToList();
        foreach (var lote in porComparar.Chunk(LoteTextosPorBuzon))
        {
            var ids = lote.Select(c => c.Id).ToList();
            var textos = await comunicaciones.RespuestasSugeridas
                .Where(r => ids.Contains(r.Id))
                .Select(r => new { r.Id, r.Texto })
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            foreach (var texto in textos.Where(t => ContieneElTexto(cuerpoNormalizado, t.Texto)))
                coinciden.Add(texto.Id);
        }

        foreach (var hilo in candidatas.GroupBy(c => c.ConversacionId))
        {
            var sugerencias = hilo.Select(c => new SugerenciaComparable(c.Estado, c.MensajeOrigenId, c.TextoHash, Id: c.Id)).ToList();
            var bloquea = sugerencias.Any(r => r.Estado == EstadoRespuestaSugerida.SinRevisar && coinciden.Contains(r.Id)) ||
                          HayCoincidenciaSinRevisar(sugerencias, r => coinciden.Contains(r.Id));

            // Solo los hilos que bloquean pagan la lectura del hilo y la comprobación de alcance.
            if (!bloquea)
                continue;

            var conversacion = await comunicaciones.Conversaciones
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == hilo.Key, cancellationToken);
            if (conversacion is not null && await alcanceDatos.ConversacionVisibleAsync(conversacion, cancellationToken))
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
        EstadoRespuestaSugerida Estado, Guid MensajeOrigenId, string TextoHash, string? Texto = null, Guid Id = default);

    private sealed record CandidataDeBuzon(
        Guid Id, Guid ConversacionId, EstadoRespuestaSugerida Estado, Guid MensajeOrigenId, string TextoHash);
}
