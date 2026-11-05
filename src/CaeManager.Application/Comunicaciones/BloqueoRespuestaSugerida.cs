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
    /// Sugerencias cuyos textos se tienen a la vez en memoria al comparar por buzón: un texto admite hasta 1 MiB, y sin lote
    /// la memoria crecería con el número de sugerencias del buzón. Con el lote es la misma sea cual sea ese número.
    /// </summary>
    private const int LoteTextosPorBuzon = 50;

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
    /// una conversación y la misma coincidencia que en <see cref="ComprobarEnvioAsync"/>: el mismo texto o un cuerpo que lo
    /// contiene entero (misma normalización, mismo umbral <see cref="LongitudMinimaTextoContenido"/>), de modo que añadir una
    /// firma o un saludo tampoco eluda el bloqueo. Una diferencia con la conversación: allí cualquier sugerencia
    /// <see cref="EstadoRespuestaSugerida.SinRevisar"/> bloquea el hilo entero; aquí solo bloquea la que coincide con el
    /// cuerpo, porque no se sabe en qué hilo caerá y una sola sin revisar frenaría todo el buzón.
    ///
    /// <para>
    /// Cómo, por orden: (1) una consulta ligera, sin el texto ni el cuerpo, trae todas las sugerencias candidatas de los
    /// hilos del buzón: las <see cref="EstadoRespuestaSugerida.Propuesta"/> y <see cref="EstadoRespuestaSugerida.SinRevisar"/>
    /// de cualquier edad y las que contestan al último entrante de su hilo, que es lo que aplica una conversación.
    /// (2) Se decide qué hilos puede ver quien envía, con la misma regla que cualquier hilo
    /// (<see cref="AlcanceDatosServiceExtensions.ConversacionVisibleAsync(IAlcanceDatosService, Guid?, Guid?, Guid?, CancellationToken)"/>),
    /// una vez por cada ancla distinta (Cliente empresarial o Empresa contraparte) de las candidatas. (3) Solo entonces se
    /// compara, y solo las candidatas de hilos visibles: hash y, para las no revisadas, los textos por lotes de
    /// <see cref="LoteTextosPorBuzon"/>, que se descartan tras compararlos.
    /// </para>
    ///
    /// <para>
    /// Solo cuentan los hilos que quien envía puede ver: un bloqueo sobre otro delataría que ese texto existe. Una
    /// sugerencia de un hilo no visible no bloquea ni desbloquea. Tampoco se nota por el tiempo: el paso (2) no depende del
    /// cuerpo y el (3) no recibe ninguna candidata de un hilo no visible, así que el trabajo de la comprobación (consultas
    /// y lecturas de texto) es el mismo con o sin coincidencia en un hilo ajeno. Solo cambia, y es ya lo que ve quien envía,
    /// si hay coincidencia en uno visible.
    /// </para>
    ///
    /// <para>
    /// Sin tope de volumen y sin fallo en cerrado por cantidad: una Revisada o una Descartada del último entrante siguen
    /// siendo candidatas mientras la contraparte no conteste, y un tope las contaría como pendientes y cerraría el buzón
    /// a los mensajes nuevos sin que quien envía pudiera arreglarlo. La memoria no crece con el buzón (lotes), pero el
    /// coste de las consultas sí crece con el historial de sugerencias del buzón, porque no hay filtro por hash: un buzón
    /// con miles de hilos con sugerencia lee miles de filas ligeras y descarga los textos de las no revisadas de hilos visibles.
    /// </para>
    /// </summary>
    public static async Task<Result> ComprobarEnvioPorBuzonAsync(
        this IComunicacionesQueryContext comunicaciones, IAlcanceDatosService alcanceDatos, Guid conexionIntegracionId, string cuerpo,
        CancellationToken cancellationToken)
    {
        var todas = await (
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
                respuesta.Id, conversacion.Id, conversacion.ClienteId, conversacion.EmpresaId,
                respuesta.Estado, respuesta.MensajeOrigenId, respuesta.TextoHash))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // El alcance se decide antes de mirar el cuerpo ni los textos: lo que hay en un hilo ajeno no se toca.
        var visibilidad = new Dictionary<(Guid? Cliente, Guid? Empresa), bool>();
        foreach (var ancla in todas.Select(c => (c.ClienteId, c.EmpresaId)).Distinct())
        {
            visibilidad[ancla] = await alcanceDatos.ConversacionVisibleAsync(
                ancla.ClienteId, ancla.EmpresaId, conexionIntegracionId, cancellationToken);
        }

        var candidatas = todas.Where(c => visibilidad[(c.ClienteId, c.EmpresaId)]).ToList();

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
            if (sugerencias.Any(r => r.Estado == EstadoRespuestaSugerida.SinRevisar && coinciden.Contains(r.Id)) ||
                HayCoincidenciaSinRevisar(sugerencias, r => coinciden.Contains(r.Id)))
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
        Guid Id, Guid ConversacionId, Guid? ClienteId, Guid? EmpresaId, EstadoRespuestaSugerida Estado, Guid MensajeOrigenId,
        string TextoHash);
}
