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
///
/// <para>
/// La misma comparación fija el origen del texto que se guarda en el Mensaje saliente (<see cref="OrigenTextoEnvio"/>): si
/// el cuerpo coincide con una sugerencia que una persona revisó, el Mensaje queda como <see cref="OrigenTextoMensaje.IaRevisada"/>
/// (Vega) o <see cref="OrigenTextoMensaje.MacroRevisada"/> y nombra la sugerencia; si no, <see cref="OrigenTextoMensaje.Persona"/>.
/// El origen nunca lo manda el cliente. Un texto que parte de una sugerencia y se edita lo bastante para dejar de
/// contenerla cuenta como escrito por la persona: la marca prueba el texto revisado, no la inspiración.
/// </para>
/// </summary>
public static class BloqueoRespuestaSugerida
{
    /// <summary>
    /// Por debajo de esta longitud, ya normalizada, una sugerencia solo se compara por igualdad: un texto tan corto puede
    /// aparecer dentro de cualquier mensaje escrito a mano.
    /// </summary>
    public const int LongitudMinimaTextoContenido = 12;

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
    /// <remarks>
    /// Si no bloquea, devuelve además el <see cref="OrigenTextoEnvio"/> con el que hay que crear el Mensaje saliente: el de
    /// la sugerencia en <see cref="EstadoRespuestaSugerida.Revisada"/> que contestaba al último entrante y cuyo texto es el
    /// cuerpo o está contenido en él, o <see cref="OrigenTextoMensaje.Persona"/> si no hay ninguna. Es la misma
    /// comparación del bloqueo, así que el origen que se guarda es el que el servidor dio por revisado. Con varias, gana la
    /// de texto idéntico, después la de texto más largo y después la revisada más tarde.
    /// </remarks>
    public static async Task<Result<OrigenTextoEnvio>> ComprobarEnvioAsync(
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
            .Select(r => new SugerenciaComparable(
                r.Estado, r.MensajeOrigenId, r.TextoHash, r.Texto, r.Id, r.Origen, r.RevisadaEnUtc))
            .ToListAsync(cancellationToken);

        if (sugerencias.Any(r => r.Estado == EstadoRespuestaSugerida.SinRevisar))
            return Result.Fallo<OrigenTextoEnvio>(SugerenciaSinRevisar);

        var hash = RespuestaSugerida.CalcularTextoHash(cuerpo);
        var cuerpoNormalizado = RespuestaSugerida.NormalizarTexto(cuerpo);

        if (HayCoincidenciaSinRevisar(sugerencias, r => r.TextoHash == hash || ContieneElTexto(cuerpoNormalizado, r.Texto!)))
            return Result.Fallo<OrigenTextoEnvio>(SugerenciaSinRevisar);

        var elegida = sugerencias
            .Where(r => r.Estado == EstadoRespuestaSugerida.Revisada && r.MensajeOrigenId == ultimoEntrante)
            .Where(r => r.TextoHash == hash || ContieneElTexto(cuerpoNormalizado, r.Texto!))
            .OrderByDescending(r => r.TextoHash == hash)
            .ThenByDescending(r => RespuestaSugerida.NormalizarTexto(r.Texto!).Length)
            .ThenByDescending(r => r.RevisadaEnUtc)
            .FirstOrDefault();

        return Result.Exito(elegida is null ? OrigenTextoEnvio.DePersona : OrigenTextoEnvio.DeSugerencia(elegida.Id, elegida.Origen));
    }

    /// <summary>
    /// Para un mensaje nuevo: el proveedor decide después de enviarlo si cae en un hilo ya existente, y solo puede ser un
    /// hilo de este buzón. Antes de enviar no se sabe cuál, así que se aplican a los hilos del buzón las mismas reglas que a
    /// una conversación, pero en SQL y solo por hash: un texto contenido no se detecta aquí, porque exigiría descargar los
    /// textos de todo el buzón. Solo cuentan los hilos que quien envía puede ver: un bloqueo sobre otro delataría que ese
    /// texto existe.
    /// </summary>
    /// <remarks>
    /// El origen sale de la misma comparación, por hash: la sugerencia en <see cref="EstadoRespuestaSugerida.Revisada"/>
    /// de un hilo de este buzón que quien envía puede ver, con el texto idéntico al cuerpo; la revisada más tarde si hay
    /// varias. Un hilo que no ve no aporta origen, por la misma razón que no bloquea.
    /// </remarks>
    public static async Task<Result<OrigenTextoEnvio>> ComprobarEnvioPorBuzonAsync(
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
            select new { Conversacion = conversacion, respuesta.Estado, respuesta.MensajeOrigenId, respuesta.TextoHash, respuesta.Id, respuesta.Origen, respuesta.RevisadaEnUtc })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var revisadas = new List<(DateTime? RevisadaEnUtc, Guid Id, OrigenRespuestaSugerida Origen)>();
        foreach (var hilo in coincidencias.GroupBy(c => c.Conversacion.Id))
        {
            var sugerencias = hilo.Select(c => new SugerenciaComparable(c.Estado, c.MensajeOrigenId, c.TextoHash)).ToList();
            var bloquea = sugerencias.Any(r => r.Estado == EstadoRespuestaSugerida.SinRevisar) ||
                          HayCoincidenciaSinRevisar(sugerencias, _ => true);
            var revisadasDelHilo = hilo.Where(c => c.Estado == EstadoRespuestaSugerida.Revisada).ToList();

            if (!bloquea && revisadasDelHilo.Count == 0)
                continue;

            if (!await alcanceDatos.ConversacionVisibleAsync(hilo.First().Conversacion, cancellationToken))
                continue;

            if (bloquea)
                return Result.Fallo<OrigenTextoEnvio>(SugerenciaSinRevisar);

            revisadas.AddRange(revisadasDelHilo.Select(c => (c.RevisadaEnUtc, c.Id, c.Origen)));
        }

        return Result.Exito(revisadas.Count == 0
            ? OrigenTextoEnvio.DePersona
            : revisadas.OrderByDescending(r => r.RevisadaEnUtc).Select(r => OrigenTextoEnvio.DeSugerencia(r.Id, r.Origen)).First());
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
        EstadoRespuestaSugerida Estado, Guid MensajeOrigenId, string TextoHash, string? Texto = null, Guid Id = default,
        OrigenRespuestaSugerida Origen = default, DateTime? RevisadaEnUtc = null);
}

/// <summary>
/// Con qué origen hay que crear el Mensaje saliente que sigue a <c>BloqueoRespuestaSugerida</c>. Lo decide el servidor
/// desde la sugerencia persistida y revisada; ningún comando lo recibe del cliente. Todo comando que cree un Mensaje
/// saliente con texto de una persona pasa por aquí, y lo vigila EnviosDeComunicacionesPasanPorElBloqueoDeSugerenciaTests.
/// </summary>
public sealed record OrigenTextoEnvio(OrigenTextoMensaje Origen, Guid? RespuestaSugeridaId)
{
    /// <summary>Sin sugerencia revisada detrás: el texto lo escribió una persona.</summary>
    public static OrigenTextoEnvio DePersona { get; } = new(OrigenTextoMensaje.Persona, null);

    public static OrigenTextoEnvio DeSugerencia(Guid respuestaSugeridaId, OrigenRespuestaSugerida origen) =>
        new(origen == OrigenRespuestaSugerida.Vega ? OrigenTextoMensaje.IaRevisada : OrigenTextoMensaje.MacroRevisada, respuestaSugeridaId);
}
