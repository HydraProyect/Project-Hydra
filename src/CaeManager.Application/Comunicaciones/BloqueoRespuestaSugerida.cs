using System.Linq.Expressions;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones;

/// <summary>
/// Bloqueo en servidor de la respuesta sugerida: nunca se envía sin que una persona la revise. Lo llaman, antes de tocar
/// el canal, todos los comandos de Comunicaciones que envían texto de una persona. La interfaz bloquea además Enviar y
/// Ctrl+Intro, pero la garantía es esta. Escribir a mano sin insertar la sugerencia no bloquea. Editar el texto cuenta como
/// revisión solo si se aparta más de <see cref="RevisionRespuestaSugerida.UmbralEdicion"/> caracteres, la misma regla que
/// aplica la interfaz: copiar la propuesta sin «Insertar» y cambiarle una letra sigue bloqueando.
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
    /// la memoria de los textos crecería con el número de sugerencias del buzón. Con el lote es la misma sea cual sea ese número.
    /// </summary>
    private const int LoteTextosPorBuzon = 50;

    public static readonly Error SugerenciaSinRevisar = Error.Crear(
        "Respuesta.SugerenciaSinRevisar", "Revisa la respuesta sugerida antes de enviar: confírmala, edítala o descártala.");

    /// <summary>
    /// Falla si la conversación tiene una sugerencia en <see cref="EstadoRespuestaSugerida.SinRevisar"/>, o si
    /// <paramref name="cuerpo"/> coincide con una en <see cref="EstadoRespuestaSugerida.Propuesta"/>, que es la vía de
    /// enviarla sin pasar por Insertar, o con una <see cref="EstadoRespuestaSugerida.Descartada"/> que contestaba al último
    /// entrante: otra persona puede tener ese texto en el editor, y descartarla no lo revisa. Coincide si es el mismo texto,
    /// si el cuerpo lo contiene entero, para que añadir una firma o un saludo no eluda el bloqueo (desde
    /// <see cref="LongitudMinimaTextoContenido"/>), o si es casi el mismo (<see cref="RevisionRespuestaSugerida.EsCasiLaMisma"/>:
    /// a lo sumo <see cref="RevisionRespuestaSugerida.UmbralEdicion"/> caracteres cambiados), para que copiarlo a mano y
    /// tocar una letra tampoco lo eluda. Una coincidencia deja de bloquear si una persona confirmó ese mismo texto para ese
    /// mismo mensaje.
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

        return HayCoincidenciaSinRevisar(sugerencias, r => r.TextoHash == hash || CoincideConElTexto(cuerpoNormalizado, r.Texto!))
            ? Result.Fallo(SugerenciaSinRevisar)
            : Result.Exito();
    }

    /// <summary>
    /// Para un mensaje nuevo: el proveedor decide después de enviarlo si cae en un hilo ya existente, y solo puede ser un
    /// hilo de este buzón con ese mismo destinatario. Antes de enviar no se sabe cuál, así que se aplican a los hilos del
    /// buzón que tienen a alguno de <paramref name="destinatarios"/> (<see cref="HilosConElDestinatario"/>) las mismas reglas que a
    /// una conversación y la misma coincidencia que en <see cref="ComprobarEnvioAsync"/>: el mismo texto, un cuerpo que lo
    /// contiene entero (misma normalización, mismo umbral <see cref="LongitudMinimaTextoContenido"/>) o uno casi igual
    /// (<see cref="RevisionRespuestaSugerida.EsCasiLaMisma"/>), de modo que añadir una firma o tocar una letra tampoco eluda
    /// el bloqueo. Una diferencia con la conversación: allí cualquier sugerencia
    /// <see cref="EstadoRespuestaSugerida.SinRevisar"/> bloquea el hilo entero; aquí solo bloquea la que coincide con el
    /// cuerpo, porque no se sabe en qué hilo caerá y una sola sin revisar frenaría todo el buzón.
    ///
    /// <para>
    /// Instantánea: toda la comprobación se ejecuta dentro de una única transacción de solo lectura REPEATABLE READ
    /// (<see cref="IComunicacionesQueryContext.EnInstantaneaDeLecturaAsync{T}"/>), que se abre al empezar y se confirma al
    /// terminar, antes de enviar. Todas las consultas (el alcance de quien envía, las candidatas, los lotes de textos) ven
    /// por tanto el mismo estado confirmado: una sugerencia creada, un cambio de estado o una reasignación de hilo
    /// confirmados después de la primera consulta no se ven, y los datos que se comparan no pueden contradecirse entre sí.
    /// Los conjuntos de alcance (cartera de Clientes empresariales y de Empresas para gestión) están memoizados por
    /// petición, y una memoización anterior a la instantánea sería una lectura de otro momento: por eso se descartan
    /// (<see cref="IInvalidadorAlcance.Invalidar"/>) al abrirla y se calculan dentro. Si el contexto ya tiene una
    /// transacción (ambiente), la comprobación se ejecuta en ella y la instantánea es la suya: solo es estricta si ya era
    /// REPEATABLE READ o superior (desde el Handler de <c>EnviarMensajeNuevo</c> no hay ninguna).
    /// </para>
    ///
    /// <para>
    /// Estructura, sobre esa instantánea: (1) se resuelve el alcance de quien envía, sin mirar el cuerpo
    /// (<see cref="HilosVisiblesDelBuzonAsync"/>): si el buzón no es visible no hay candidatas, y si lo es, los conjuntos de
    /// cartera pasan a ser un filtro. (2) Una consulta, sin el texto ni el cuerpo, trae las sugerencias candidatas de los
    /// hilos del buzón que cumplen ese filtro: las <see cref="EstadoRespuestaSugerida.Propuesta"/> y
    /// <see cref="EstadoRespuestaSugerida.SinRevisar"/> de cualquier edad y las que contestan al último entrante de su hilo,
    /// que es lo que aplica una conversación. (3) Coinciden con el cuerpo, por hash, las no revisadas con el hash del
    /// cuerpo; por contención o parecido, las demás no revisadas cuyo texto, leído por lotes de <see cref="LoteTextosPorBuzon"/>
    /// y descartado tras compararlo, está contenido en el cuerpo o es casi el mismo (distancia acotada, lineal en la longitud). (4) Sin ninguna coincidencia no se bloquea; con ellas se
    /// evalúa el bloqueo sobre las mismas candidatas, con las reglas de la conversación: la Revisada que desbloquea es la
    /// de esa misma instantánea. Como todas las lecturas son de la misma foto, no hay que releer ni volver a validar nada.
    /// </para>
    ///
    /// <para>
    /// Solo cuentan los hilos que quien envía puede ver: un bloqueo sobre otro delataría que ese texto existe. El filtro de
    /// alcance es parte de todas las consultas, así que un hilo no visible no llega nunca a memoria: no bloquea, no
    /// desbloquea y no provoca ninguna lectura de texto, con o sin coincidencia. Por eso tampoco se nota por el tiempo. El
    /// filtro es la regla de
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
    /// sugerencia de hilo visible) y un acceso por lote de textos de las no revisadas; el trabajo en memoria es lineal.
    /// Crece con el historial de sugerencias del buzón, porque no hay filtro por hash. También la memoria crece con ese
    /// historial, incluidas las Revisadas: las filas ligeras de todas las candidatas están a la vez en memoria (decenas de
    /// bytes de Id, estado y hash por sugerencia). Lo que los lotes mantienen constante es la memoria de los textos, que son
    /// lo grande: como mucho <see cref="LoteTextosPorBuzon"/> a la vez. La transacción dura lo que dure esa lectura y no
    /// toma bloqueos de escritura.
    /// </para>
    ///
    /// <para>
    /// Límite aceptado: la comprobación se hace sobre una instantánea REPEATABLE READ, y los cambios confirmados después de
    /// ella (la reasignación de un hilo, un cambio de estado, una sugerencia nueva) entre la instantánea y el envío quedan
    /// fuera de lo que esta garantiza: es inherente a cualquier comprobación previa al envío.
    /// </para>
    /// </summary>
    public static Task<Result> ComprobarEnvioPorBuzonAsync(
        this IComunicacionesQueryContext comunicaciones, IAlcanceDatosService alcanceDatos, IInvalidadorAlcance invalidadorAlcance,
        Guid conexionIntegracionId, IEnumerable<string> destinatarios, string cuerpo, CancellationToken cancellationToken) =>
        comunicaciones.EnInstantaneaDeLecturaAsync(async ct =>
        {
            // Dentro de la instantánea y antes de la primera lectura de alcance: sin la memoización de antes de abrirla.
            invalidadorAlcance.Invalidar();

            var hilosVisibles = await HilosVisiblesDelBuzonAsync(alcanceDatos, conexionIntegracionId, ct);
            if (hilosVisibles is null)
                return Result.Exito();

            var hilos = HilosConElDestinatario(comunicaciones, hilosVisibles, DireccionesNormalizadas(destinatarios));
            var candidatas = await Candidatas(comunicaciones, hilos).ToListAsync(ct);

            var hash = RespuestaSugerida.CalcularTextoHash(cuerpo);
            var cuerpoNormalizado = RespuestaSugerida.NormalizarTexto(cuerpo);

            // Una Revisada solo desbloquea a las demás y no coincide nunca.
            var coinciden = candidatas
                .Where(c => c.Estado != EstadoRespuestaSugerida.Revisada && c.TextoHash == hash).Select(c => c.Id).ToHashSet();
            var porComparar = candidatas.Where(c => c.Estado != EstadoRespuestaSugerida.Revisada && !coinciden.Contains(c.Id)).ToList();
            foreach (var lote in porComparar.Chunk(LoteTextosPorBuzon))
            {
                var ids = lote.Select(c => c.Id).ToList();
                // El mismo filtro de alcance que en las demás consultas, aunque la instantánea ya lo garantice: un hilo
                // oculto no debe llegar nunca a memoria.
                var textos = await (
                    from r in comunicaciones.RespuestasSugeridas
                    where ids.Contains(r.Id)
                    join c in hilos on r.ConversacionId equals c.Id
                    select new { r.Id, r.Texto })
                    .AsNoTracking()
                    .ToListAsync(ct);

                foreach (var texto in textos.Where(t => CoincideConElTexto(cuerpoNormalizado, t.Texto)))
                    coinciden.Add(texto.Id);
            }

            if (coinciden.Count == 0)
                return Result.Exito();

            var porHilo = candidatas
                .GroupBy(c => c.ConversacionId)
                .Select(hilo => hilo.Select(c => new SugerenciaComparable(c.Estado, c.MensajeOrigenId, c.TextoHash, Id: c.Id)).ToList())
                .ToList();

            return Bloquea(porHilo, coinciden) ? Result.Fallo(SugerenciaSinRevisar) : Result.Exito();
        }, cancellationToken);

    /// <summary>
    /// Las sugerencias candidatas de los hilos visibles del buzón (columnas ligeras, sin el texto), con la condición de
    /// candidata de una conversación: activa, o que contesta al último entrante. El filtro de alcance va siempre.
    /// </summary>
    private static IQueryable<CandidataDeBuzon> Candidatas(IComunicacionesQueryContext comunicaciones, IQueryable<Conversacion> hilos) =>
        (
            from respuesta in comunicaciones.RespuestasSugeridas
            join conversacion in hilos on respuesta.ConversacionId equals conversacion.Id
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
            .AsNoTracking();

    /// <summary>
    /// De los hilos visibles del buzón, los que tienen al destinatario del envío (revisión Codex r2 de la #2163, MEDIO 4):
    /// una de las direcciones a las que va el mensaje (Para o CC) figura como participante del hilo o como remitente de uno
    /// de sus entrantes. Antes de enviar no hay conversación identificable, y el proveedor solo agrupa un mensaje nuevo en un
    /// hilo de ese mismo destinatario; una sugerencia de la conversación con otra contraparte contesta a otra persona y no
    /// bloquea un mensaje independiente, aunque su texto diste 15 caracteres o menos. Correo sin distinguir mayúsculas.
    /// </summary>
    private static IQueryable<Conversacion> HilosConElDestinatario(
        IComunicacionesQueryContext comunicaciones, Expression<Func<Conversacion, bool>> hilosVisibles, List<string> direcciones) =>
        comunicaciones.Conversaciones.Where(hilosVisibles).Where(conversacion =>
            comunicaciones.ParticipantesConversacion.Any(p =>
                p.ConversacionId == conversacion.Id && direcciones.Contains(p.Email.ToLower())) ||
            comunicaciones.Mensajes.Any(m =>
                m.ConversacionId == conversacion.Id && m.Direccion == DireccionMensaje.Entrante &&
                direcciones.Contains(m.Remitente.ToLower())));

    private static List<string> DireccionesNormalizadas(IEnumerable<string> destinatarios) =>
        destinatarios
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static bool Bloquea(IReadOnlyList<List<SugerenciaComparable>> hilos, HashSet<Guid> coinciden) =>
        hilos.Any(sugerencias =>
            sugerencias.Any(r => r.Estado == EstadoRespuestaSugerida.SinRevisar && coinciden.Contains(r.Id)) ||
            HayCoincidenciaSinRevisar(sugerencias, r => coinciden.Contains(r.Id)));

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

    /// <summary>
    /// El cuerpo contiene el texto entero (desde <see cref="LongitudMinimaTextoContenido"/>) o es casi el mismo
    /// (<see cref="RevisionRespuestaSugerida.EsCasiLaMisma"/>): copiar la propuesta a mano y cambiarle una letra no la revisa.
    /// </summary>
    private static bool CoincideConElTexto(string cuerpoNormalizado, string texto)
    {
        var textoNormalizado = RespuestaSugerida.NormalizarTexto(texto);
        return (textoNormalizado.Length >= LongitudMinimaTextoContenido &&
                cuerpoNormalizado.Contains(textoNormalizado, StringComparison.Ordinal)) ||
               RevisionRespuestaSugerida.EsCasiLaMisma(cuerpoNormalizado, textoNormalizado);
    }

    private sealed record SugerenciaComparable(
        EstadoRespuestaSugerida Estado, Guid MensajeOrigenId, string TextoHash, string? Texto = null, Guid Id = default);

    private sealed record CandidataDeBuzon(
        Guid Id, Guid ConversacionId, EstadoRespuestaSugerida Estado, Guid MensajeOrigenId, string TextoHash);
}
