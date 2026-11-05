using System.Linq.Expressions;
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
    /// Cómo, por orden: (1) se resuelve el alcance de quien envía, una sola vez y sin mirar el cuerpo
    /// (<see cref="HilosVisiblesDelBuzonAsync"/>): si el buzón no es visible no hay candidatas, y si lo es, los conjuntos
    /// memoizados de Clientes empresariales visibles y de Empresas para gestión pasan a ser un filtro. (2) Una única
    /// consulta, sin el texto ni el cuerpo, trae las sugerencias candidatas de los hilos del buzón que cumplen ese filtro:
    /// las <see cref="EstadoRespuestaSugerida.Propuesta"/> y <see cref="EstadoRespuestaSugerida.SinRevisar"/> de cualquier
    /// edad y las que contestan al último entrante de su hilo, que es lo que aplica una conversación. (3) Se compara con el
    /// cuerpo: hash y, para las no revisadas, los textos por lotes de <see cref="LoteTextosPorBuzon"/>, que se descartan
    /// tras compararlos. Una Revisada se busca por (hash, mensaje de origen) en un conjunto del hilo, en tiempo constante.
    /// </para>
    ///
    /// <para>
    /// Solo cuentan los hilos que quien envía puede ver: un bloqueo sobre otro delataría que ese texto existe. El filtro de
    /// alcance es parte de la consulta de candidatas, así que el hilo, su ancla y su visibilidad salen de la misma lectura
    /// (una reasignación del hilo no deja una visibilidad obsoleta), y un hilo no visible no llega nunca a memoria: no
    /// bloquea, no desbloquea y no provoca ninguna lectura de texto, con o sin coincidencia. Por eso tampoco se nota por el
    /// tiempo. El filtro es la regla de
    /// <see cref="AlcanceDatosServiceExtensions.ConversacionVisibleAsync(IAlcanceDatosService, Guid?, Guid?, Guid?, CancellationToken)"/>
    /// traducida a una expresión: buzón visible, y con Empresa contraparte que esté en la cartera de gestión; sin ella, que
    /// no haya Cliente empresarial (triage compartido) o que esté entre los visibles; un conjunto nulo es acceso total.
    /// Un test compara ambas sobre los mismos casos para que no diverjan.
    /// </para>
    ///
    /// <para>
    /// Sin tope de volumen y sin fallo en cerrado por cantidad: una Revisada o una Descartada del último entrante siguen
    /// siendo candidatas mientras la contraparte no conteste, y un tope las contaría como pendientes y cerraría el buzón
    /// a los mensajes nuevos sin que quien envía pudiera arreglarlo. Coste: una consulta de candidatas (una fila ligera por
    /// sugerencia de hilo visible) más un acceso por lote de textos de las no revisadas, y el trabajo en memoria es lineal;
    /// ambos crecen con el historial de sugerencias del buzón, porque no hay filtro por hash. La memoria no crece con el
    /// buzón (lotes).
    /// </para>
    /// </summary>
    public static async Task<Result> ComprobarEnvioPorBuzonAsync(
        this IComunicacionesQueryContext comunicaciones, IAlcanceDatosService alcanceDatos, Guid conexionIntegracionId, string cuerpo,
        CancellationToken cancellationToken)
    {
        var hilosVisibles = await HilosVisiblesDelBuzonAsync(alcanceDatos, conexionIntegracionId, cancellationToken);
        if (hilosVisibles is null)
            return Result.Exito();

        var candidatas = await (
            from respuesta in comunicaciones.RespuestasSugeridas
            join conversacion in comunicaciones.Conversaciones.Where(hilosVisibles) on respuesta.ConversacionId equals conversacion.Id
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
            .AsNoTracking()
            .ToListAsync(cancellationToken);

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

    /// <summary>
    /// Los hilos de un buzón que quien envía puede ver, como filtro para la consulta de candidatas; <c>null</c> si el
    /// buzón no es visible. Es la regla de <c>ConversacionVisibleAsync</c>: buzón visible (una vez, es el mismo para todos
    /// los hilos); con Empresa contraparte, que esté en <see cref="IAlcanceDatosService.ObtenerEmpresaIdsParaGestionAsync"/>;
    /// sin ella, que no tenga Cliente empresarial (triage compartido) o que esté en
    /// <see cref="IAlcanceDatosService.ObtenerClienteIdsVisiblesAsync"/>. Un conjunto nulo es acceso total. Los dos
    /// conjuntos están memoizados por petición: no hay una consulta por hilo.
    /// </summary>
    internal static async Task<Expression<Func<Conversacion, bool>>?> HilosVisiblesDelBuzonAsync(
        IAlcanceDatosService alcanceDatos, Guid conexionIntegracionId, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ConexionIntegracionVisibleAsync(conexionIntegracionId, cancellationToken))
            return null;

        var carteraDeClientes = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        var carteraDeEmpresas = await alcanceDatos.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);
        var todosLosClientes = carteraDeClientes is null;
        var todasLasEmpresas = carteraDeEmpresas is null;
        var clientes = carteraDeClientes?.ToList() ?? [];
        var empresas = carteraDeEmpresas?.ToList() ?? [];

        return c => c.ConexionIntegracionId == conexionIntegracionId &&
                    (c.EmpresaId != null
                        ? todasLasEmpresas || empresas.Contains(c.EmpresaId.Value)
                        : c.ClienteId == null || todosLosClientes || clientes.Contains(c.ClienteId.Value));
    }

    private static bool HayCoincidenciaSinRevisar(
        IReadOnlyCollection<SugerenciaComparable> sugerencias, Func<SugerenciaComparable, bool> coincide)
    {
        var revisadas = sugerencias
            .Where(r => r.Estado == EstadoRespuestaSugerida.Revisada)
            .Select(r => (r.TextoHash, r.MensajeOrigenId))
            .ToHashSet();

        return sugerencias.Any(p =>
            p.Estado is EstadoRespuestaSugerida.Propuesta or EstadoRespuestaSugerida.Descartada &&
            coincide(p) &&
            !revisadas.Contains((p.TextoHash, p.MensajeOrigenId)));
    }

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
