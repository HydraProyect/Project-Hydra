using CaeManager.Application.Common;
using CaeManager.Domain.Integraciones;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones;

/// <summary>Cómo terminó la pasada de la recuperación del hueco sobre una conexión.</summary>
public enum ResultadoRecuperacionHueco
{
    /// <summary>No había nada que recuperar o la conexión no está operativa: no se llamó a Graph.</summary>
    SinRecuperar,

    /// <summary>El cerrojo de la conexión estaba ocupado (Desconectar, Reactivar o la verificación): ciclo siguiente.</summary>
    Ocupada,

    /// <summary>
    /// Se leyó parte del listado (tope de mensajes o de páginas de la pasada): lo leído quedó encolado y el cursor
    /// guardado; la pasada siguiente sigue desde él.
    /// </summary>
    Parcial,

    /// <summary>
    /// Una página con más detrás no sale del segundo del cursor y el segundo no se puede recorrer por Id (página vacía,
    /// recepciones con fracción o un recorrido incoherente): pedirla otra vez desde él devolvería la misma. No se
    /// encola, el cursor no avanza, el listado no termina y la marca se queda (falla cerrado); la fila guarda el segundo
    /// (<see cref="ConexionIntegracion.RecuperacionHuecoAtascadaEnUtc"/>), Conexiones lo avisa y se registra.
    /// </summary>
    Atascada,

    /// <summary>Una página falló (o la conexión dejó de estar operativa entre medias): la marca y el cursor se quedan.</summary>
    Interrumpida,

    /// <summary>Otra operación registró una pérdida o cambió la conexión: la recuperación se anuló y vuelve a empezar.</summary>
    Anulada,

    /// <summary>El listado está completo, pero algún evento de recuperación sigue sin ingerir: la marca se queda.</summary>
    PendienteIngesta,

    /// <summary>
    /// El listado está completo y todo ingerido, pero la marca es anterior al tope de antigüedad: el correo de antes no
    /// se ha leído y la marca se queda.
    /// </summary>
    Truncada,

    /// <summary>
    /// El listado está completo y todo ingerido, pero recorrió algún segundo por Id (cursor compuesto, issue #2263), un
    /// recorrido aún sin verificar contra Graph real: la marca se queda
    /// (<see cref="ConexionIntegracion.RecuperacionHuecoRecorridaPorId"/>).
    /// </summary>
    RecorridaPorIdSinVerificar,

    /// <summary>Lectura completa, todo ingerido, conexión operativa con suscripción viva: la marca se limpió.</summary>
    Limpiada,
}

/// <summary>
/// Recuperación del hueco de correo de Microsoft 365 (issue #2124): lee la Bandeja de entrada desde
/// <see cref="ConexionIntegracion.HuecoPendienteDesdeUtc"/> y limpia la marca solo tras una lectura completa. La llama
/// <c>RecuperacionHuecoCorreoHostedService</c> por Tenant propietario activo, con su ámbito explícito; todo lo que lee y
/// escribe pasa por los repositorios del Tenant de la sesión (filtro de Tenant y RLS).
/// <list type="bullet">
/// <item><b>Cada página desde el cursor.</b> <c>receivedDateTime ge</c> el cursor de la recuperación
/// (<see cref="ConexionIntegracion.RecuperacionHuecoCursorUtc"/>), en orden ascendente y con páginas grandes, para que
/// los topes no se gasten en el correo reciente, que ya entra por el webhook. Nunca se sigue <c>@odata.nextLink</c>: la
/// continuación de Graph es posicional (<c>$skip</c>) y, si un mensaje ya leído sale de la carpeta entre dos páginas,
/// salta uno sin leer. Su presencia solo dice que hay más; el listado queda completo con una página sin ella. Un 200
/// ilegible es un fallo (lo decide el adaptador), y la marca y el cursor se quedan.</item>
/// <item><b>Empates.</b> Cada página repite los mensajes del segundo del cursor: dentro de la pasada se descartan por Id
/// antes de encolar. Entre pasadas no se encola lo ya ingerido ni lo que ya está en un evento de recuperación Pendiente
/// o Procesando, y la ingesta deduplica el resto por <c>MensajeExternoId</c>. Si una página con más detrás
/// no sale de ese mismo segundo, el cursor pasa a ser compuesto (recepción, Id), issue #2263: el segundo se recorre por
/// Id (<see cref="IMicrosoft365GraphClient.ListarRecibidosEnSegundoAsync"/>), guardando el último Id encolado
/// (<see cref="ConexionIntegracion.RecuperacionHuecoCursorId"/>) con cada página, y al leerlo entero el listado sigue por
/// fecha desde el segundo siguiente. Lo leído así se encola, pero la marca no se limpia
/// (<see cref="ResultadoRecuperacionHueco.RecorridaPorIdSinVerificar"/>) hasta verificar el recorrido contra Graph real. Si
/// el segundo no se puede recorrer así (página vacía, recepciones con fracción o un
/// recorrido incoherente), la página no se encola, la recuperación se queda
/// <see cref="ResultadoRecuperacionHueco.Atascada"/> en ese segundo y la marca no se limpia.</item>
/// <item><b>Una página, un guardado.</b> Lo nuevo de cada página se encola en eventos de recuperación
/// (<see cref="EventoWebhook.CrearRecuperacion"/>, a lo sumo <see cref="MaximoMensajesPorEvento"/> Ids cada uno) en el
/// MISMO guardado que avanza el cursor: o quedan los dos o ninguno.</item>
/// <item><b>Topes.</b> <see cref="MaximoMensajesPorPasada"/> mensajes o <see cref="MaximoPaginasPorPasada"/> páginas
/// por pasada (la siguiente sigue desde el cursor) y <see cref="ConexionIntegracion.AntiguedadMaximaRecuperacion"/> de
/// antigüedad (la marca anterior se queda: <see cref="ResultadoRecuperacionHueco.Truncada"/>). Los dos se avisan en el
/// registro.</item>
/// <item><b>Misma puerta que la ingesta.</b> Antes de cada llamada a Graph se relee de la base si la conexión sigue
/// operativa y verificada (<see cref="IConexionIntegracionRepository.SigueOperativaVerificadaAsync"/>). La ingesta de
/// los eventos deduplica por <c>MensajeExternoId</c>, como con cualquier notificación.</item>
/// <item><b>Limpieza.</b> Solo con el listado completo, ningún evento de recuperación sin ingerir, la conexión operativa
/// y verificada y su suscripción viva, releído todo de la base bajo el cerrojo de la conexión. Cualquier pérdida
/// registrada mientras tanto anula la recuperación en la propia fila (<see cref="ConexionIntegracion.RecuperacionHuecoCubreDesdeUtc"/>),
/// y la concurrencia optimista de la conexión impide guardar la limpieza sobre una fila que otro cambió después de
/// releerla. Un evento de recuperación descartado retrocede la marca a lo que cubría
/// (<see cref="ConexionIntegracion.RegistrarNotificacionDescartada"/>).</item>
/// </list>
/// </summary>
public class RecuperacionHuecoCorreoService(
    IConexionIntegracionRepository conexionRepositorio,
    IEventoWebhookRepository eventoRepositorio,
    IMensajesExternosIngeridos mensajesIngeridos,
    ISuscripcionWebhookRepository suscripcionRepositorio,
    IMicrosoft365GraphClient graphClient,
    AccesoGraphService accesoGraph,
    IUnitOfWork unitOfWork,
    ICerrojoVerificacionBuzon cerrojo,
    IDescarteCambiosPendientes descarteCambios,
    ILogger<RecuperacionHuecoCorreoService> logger)
{
    /// <summary>Tope de mensajes listados por conexión y pasada.</summary>
    public const int MaximoMensajesPorPasada = 5000;

    /// <summary>Tope de páginas por conexión y pasada.</summary>
    public const int MaximoPaginasPorPasada = 10;

    /// <summary>Tope de Ids por evento de recuperación: un mensaje que falla reintenta su evento, no la página entera.</summary>
    public const int MaximoMensajesPorEvento = 50;

    /// <summary>Cada conexión candidata del Tenant actual, aislada de las demás.</summary>
    public async Task RecuperarDelTenantActualAsync(CancellationToken cancellationToken)
    {
        foreach (var conexionId in await conexionRepositorio.ObtenerIdsConHuecoPendienteAsync(cancellationToken))
        {
            try
            {
                var resultado = await RecuperarConexionAsync(conexionId, cancellationToken);
                logger.LogInformation("Recuperación del hueco de correo de {ConexionId}: {Resultado}.", conexionId, resultado);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Una colisión de concurrencia (otro proceso guardó la conexión) o un fallo de la base: lo rastreado se
                // descarta y la conexión se reintenta en el ciclo siguiente, desde lo que haya en la base.
                logger.LogWarning(ex, "Falló la recuperación del hueco de correo de {ConexionId}; se reintenta en el ciclo siguiente.", conexionId);
                descarteCambios.DescartarCambiosPendientes();
            }
        }
    }

    /// <summary>Una pasada sobre una conexión del Tenant actual.</summary>
    public async Task<ResultadoRecuperacionHueco> RecuperarConexionAsync(Guid conexionId, CancellationToken cancellationToken)
    {
        var inicio = await IniciarAsync(conexionId, cancellationToken);
        if (inicio.Resultado is { } sinListar)
            return sinListar;

        var recuperacion = inicio.Recuperacion!.Value;
        if (!recuperacion.ListadoCompleto)
        {
            var listado = await ListarAsync(conexionId, inicio.Conexion!, inicio.AccessToken!, recuperacion, cancellationToken);
            if (listado.Resultado is { } corte)
                return corte;

            recuperacion = listado.Recuperacion!.Value;
        }

        return await IntentarLimpiarAsync(conexionId, recuperacion, cancellationToken);
    }

    /// <summary>Lo que la pasada espera encontrar en la fila: si cambia, alguien anuló o rehízo la recuperación.</summary>
    private readonly record struct EstadoRecuperacion(
        Guid Id, DateTime CubreDesdeUtc, DateTime CursorUtc, string? CursorId, bool ListadoCompleto)
    {
        public static EstadoRecuperacion De(ConexionIntegracion conexion) =>
            new(conexion.RecuperacionHuecoId!.Value, conexion.RecuperacionHuecoCubreDesdeUtc!.Value, conexion.RecuperacionHuecoCursorUtc!.Value,
                conexion.RecuperacionHuecoCursorId, conexion.RecuperacionHuecoListadoCompleto);

        public bool Coincide(ConexionIntegracion conexion) =>
            conexion.RecuperacionHuecoId == Id
            && conexion.RecuperacionHuecoCubreDesdeUtc == CubreDesdeUtc
            && conexion.RecuperacionHuecoCursorUtc == CursorUtc
            && string.Equals(conexion.RecuperacionHuecoCursorId, CursorId, StringComparison.Ordinal)
            && conexion.RecuperacionHuecoListadoCompleto == ListadoCompleto;
    }

    private sealed record Inicio(
        ResultadoRecuperacionHueco? Resultado, ConexionIntegracion? Conexion = null, string? AccessToken = null,
        EstadoRecuperacion? Recuperacion = null);

    /// <summary>
    /// Bajo el cerrojo: relee la conexión, empieza o retoma la recuperación y, si hay que listar, obtiene el token. Guarda
    /// el inicio (y una rotación del refresh token) antes de la primera página: desde ese guardado, cualquier pérdida que
    /// se registre la anula en la fila.
    /// </summary>
    private async Task<Inicio> IniciarAsync(Guid conexionId, CancellationToken cancellationToken)
    {
        Inicio? inicio = null;
        var ejecutado = await cerrojo.IntentarEjecutarAsync(conexionId, async ct =>
        {
            var ahoraUtc = DateTime.UtcNow;
            var conexion = await conexionRepositorio.ObtenerActualPorIdAsync(conexionId, ct);
            if (conexion is null || conexion.HuecoPendienteDesdeUtc is null || !conexion.OperativaVerificada(ahoraUtc))
            {
                inicio = new Inicio(ResultadoRecuperacionHueco.SinRecuperar);
                return;
            }

            var empieza = !conexion.RecuperacionHuecoEnCurso;
            conexion.IniciarRecuperacionHueco(ahoraUtc);
            if (conexion.RecuperacionHuecoTruncada && empieza)
                logger.LogWarning(
                    "La marca del hueco de {ConexionId} es anterior al tope de {Dias} días: se recupera desde {CubreDesde} y la marca se queda.",
                    conexionId, ConexionIntegracion.AntiguedadMaximaRecuperacion.TotalDays, conexion.RecuperacionHuecoCubreDesdeUtc);

            string? accessToken = null;
            if (!conexion.RecuperacionHuecoListadoCompleto)
            {
                // Sin la cancelación del llamador: un canje que ya rotó el refresh token tiene que llegar a guardarse.
                var token = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion, CancellationToken.None);
                if (token.EsFallido)
                {
                    logger.LogWarning("Sin token para recuperar el hueco de {ConexionId}: {Error}.", conexionId, token.Error.Mensaje);
                    // El inicio y una rotación del refresh token se guardan igualmente: no se pierde nada por guardarlos.
                    await unitOfWork.SaveChangesAsync(CancellationToken.None);
                    inicio = new Inicio(ResultadoRecuperacionHueco.Interrumpida);
                    return;
                }

                accessToken = token.Valor;
            }

            // Sin la cancelación del llamador: un apagado a mitad no debe dejar un refresh token rotado sin guardar.
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            inicio = new Inicio(null, conexion, accessToken, EstadoRecuperacion.De(conexion));
        }, cancellationToken);

        return ejecutado ? inicio! : new Inicio(ResultadoRecuperacionHueco.Ocupada);
    }

    private sealed record Listado(ResultadoRecuperacionHueco? Resultado, EstadoRecuperacion? Recuperacion = null);

    /// <summary>
    /// Lista página a página, cada una pedida desde el cursor que dejó la anterior, hasta una página sin más detrás, un
    /// tope o un atasco.
    /// </summary>
    private async Task<Listado> ListarAsync(
        Guid conexionId, ConexionIntegracion conexion, string accessToken, EstadoRecuperacion recuperacion,
        CancellationToken cancellationToken)
    {
        var buzon = BuzonGraph.De(conexion);
        // Ids ya encolados en esta pasada: cada página repite los mensajes del segundo del cursor (ge).
        var encolados = new HashSet<string>(StringComparer.Ordinal);
        var mensajes = 0;
        // Recorriendo por Id el segundo del cursor (issue #2263): retomado de la fila o porque una página no salió de él.
        var enSegundo = recuperacion.CursorId is not null;

        for (var pagina = 1; ; pagina++)
        {
            // La misma puerta que la ingesta, antes de cada llamada a Graph.
            if (!await conexionRepositorio.SigueOperativaVerificadaAsync(conexionId, DateTime.UtcNow, cancellationToken))
            {
                logger.LogWarning("La conexión {ConexionId} dejó de estar operativa durante la recuperación del hueco: se corta.", conexionId);
                return new Listado(ResultadoRecuperacionHueco.Interrumpida);
            }

            var desdeUtc = recuperacion.CursorUtc;
            var segundoDelCursor = ConexionIntegracion.SegundoDe(desdeUtc);
            var respuesta = enSegundo
                ? await graphClient.ListarRecibidosEnSegundoAsync(accessToken, buzon, segundoDelCursor, recuperacion.CursorId, cancellationToken)
                : await graphClient.ListarRecibidosDesdeAsync(accessToken, buzon, desdeUtc, cancellationToken);
            if (respuesta.EsFallido)
            {
                logger.LogWarning(
                    "Falló la página {Pagina} de la recuperación del hueco de {ConexionId}: {Error}. La marca y el cursor se quedan.",
                    pagina, conexionId, respuesta.Error.Mensaje);
                return new Listado(ResultadoRecuperacionHueco.Interrumpida);
            }

            var leida = respuesta.Valor;
            var ultimoRecibidoUtc = leida.Mensajes.Count > 0 ? leida.Mensajes.Max(m => m.RecibidoUtc) : (DateTime?)null;

            if (enSegundo)
            {
                // Una página vacía con más detrás no deja desempate al que avanzar; y si al entrar en el segundo no vuelve
                // nada, el eq no ve lo que el ge sí vio (Graph guardaría la recepción con más precisión de la que
                // devuelve). En los dos casos, avanzar saltaría correo: falla cerrado.
                if (leida.Mensajes.Count == 0 && (leida.HayMas || recuperacion.CursorId is null))
                {
                    logger.LogWarning(
                        "La recuperación del hueco de {ConexionId} está detenida en {Segundo}: el recorrido por Id de ese segundo no es coherente. No se encola, el cursor no avanza y la marca se queda.",
                        conexionId, segundoDelCursor);
                    return await RegistrarAtascoAsync(conexionId, recuperacion, cancellationToken);
                }

                var ultimoId = leida.Mensajes.Count > 0 ? leida.Mensajes[^1].MensajeExternoId : null;
                var enElSegundo = await GuardarPaginaAsync(
                    conexionId, recuperacion, leida.Mensajes.Where(m => !encolados.Contains(m.MensajeExternoId)).ToList(),
                    conexion => conexion.AvanzarRecuperacionHuecoEnSegundo(ultimoId, leida.HayMas), cancellationToken);
                if (enElSegundo.Resultado is { } cortada)
                    return new Listado(cortada);

                encolados.UnionWith(leida.Mensajes.Select(m => m.MensajeExternoId));
                recuperacion = enElSegundo.Recuperacion!.Value;
                mensajes += leida.Mensajes.Count;
                // Segundo leído entero: el cursor ya está en el siguiente y el listado sigue por fecha. No termina aquí.
                if (!leida.HayMas)
                    enSegundo = false;

                if (mensajes >= MaximoMensajesPorPasada || pagina >= MaximoPaginasPorPasada)
                    return TopeDeLaPasada(conexionId, mensajes, pagina, recuperacion);

                continue;
            }

            // Una página con más detrás que no trae ningún mensaje de un segundo posterior al del cursor (≥ 1000 correos
            // recibidos en ese mismo segundo, o una página parcial de Graph cuyo máximo es el cursor): pedirla otra vez
            // desde él devolvería la misma. Se detecta ANTES de encolar y la página no se guarda. Si todos sus mensajes son
            // de ese segundo exacto, sin fracción, el segundo se recorre por Id (cursor compuesto, issue #2263) desde la
            // página siguiente. Si no (página vacía o recepciones con fracción, que eq el segundo no encontraría), el cursor
            // no avanza, el listado no termina y la marca se queda; la fila guarda el segundo de la detención, que
            // Conexiones avisa, y se registra en cada pasada. Falla cerrado: avisa de más, nunca de menos.
            if (leida.HayMas && (ultimoRecibidoUtc is not { } ultimo
                || ConexionIntegracion.SegundoDe(ultimo) <= segundoDelCursor))
            {
                if (leida.Mensajes.Count > 0 && leida.Mensajes.All(m => m.RecibidoUtc == segundoDelCursor))
                {
                    logger.LogInformation(
                        "La recuperación del hueco de {ConexionId} recorre por Id el segundo {Segundo}: no cabe en una página.",
                        conexionId, segundoDelCursor);
                    enSegundo = true;
                    // La página leída cuenta para el tope aunque no se guarde: sin esta comprobación, el cambio al recorrido
                    // por Id pediría una página más de las MaximoPaginasPorPasada. El cursor no se ha movido, así que la
                    // pasada siguiente vuelve a detectar el segundo y entra en el recorrido por Id desde su primera página.
                    if (pagina >= MaximoPaginasPorPasada)
                        return TopeDeLaPasada(conexionId, mensajes, pagina, recuperacion);

                    continue;
                }

                logger.LogWarning(
                    "La recuperación del hueco de {ConexionId} está detenida en {Segundo}: una página con más detrás no sale de ese segundo y no se puede recorrer por Id. No se encola, el cursor no avanza y la marca se queda.",
                    conexionId, segundoDelCursor);
                return await RegistrarAtascoAsync(conexionId, recuperacion, cancellationToken);
            }

            var nuevos = leida.Mensajes.Where(m => !encolados.Contains(m.MensajeExternoId)).ToList();
            var guardada = await GuardarPaginaAsync(
                conexionId, recuperacion, nuevos, conexion => conexion.AvanzarRecuperacionHueco(ultimoRecibidoUtc, leida.HayMas),
                cancellationToken);
            if (guardada.Resultado is { } corte)
                return new Listado(corte);

            encolados.UnionWith(nuevos.Select(m => m.MensajeExternoId));
            recuperacion = guardada.Recuperacion!.Value;
            mensajes += leida.Mensajes.Count;

            if (!leida.HayMas)
                return new Listado(null, recuperacion);

            if (mensajes >= MaximoMensajesPorPasada || pagina >= MaximoPaginasPorPasada)
                return TopeDeLaPasada(conexionId, mensajes, pagina, recuperacion);
        }
    }

    private Listado TopeDeLaPasada(Guid conexionId, int mensajes, int pagina, EstadoRecuperacion recuperacion)
    {
        logger.LogWarning(
            "La recuperación del hueco de {ConexionId} alcanzó el tope de la pasada ({Mensajes} mensajes, {Paginas} páginas): el ciclo siguiente sigue desde el cursor {Cursor}.",
            conexionId, mensajes, pagina, recuperacion.CursorUtc);
        return new Listado(ResultadoRecuperacionHueco.Parcial);
    }

    /// <summary>
    /// Bajo el cerrojo, en un único guardado: los eventos de recuperación con lo nuevo de la página y el avance del
    /// cursor. Si la fila ya no tiene la recuperación que se estaba siguiendo, no se guarda nada.
    /// </summary>
    private async Task<Listado> GuardarPaginaAsync(
        Guid conexionId, EstadoRecuperacion esperada, IReadOnlyList<MensajeRecibidoGraphDto> nuevos,
        Action<ConexionIntegracion> avanzarCursor, CancellationToken cancellationToken)
    {
        Listado? listado = null;
        var ejecutado = await cerrojo.IntentarEjecutarAsync(conexionId, async ct =>
        {
            var conexion = await conexionRepositorio.ObtenerActualPorIdAsync(conexionId, ct);
            if (conexion is null || !esperada.Coincide(conexion))
            {
                listado = new Listado(ResultadoRecuperacionHueco.Anulada);
                return;
            }

            var unicos = await SinYaIngeridosNiEnCursoAsync(
                conexionId, nuevos.DistinctBy(m => m.MensajeExternoId, StringComparer.Ordinal).ToList(), ct);
            foreach (var lote in unicos.Chunk(MaximoMensajesPorEvento))
                eventoRepositorio.Agregar(EventoWebhook.CrearRecuperacion(
                    conexionId, graphClient.ConstruirPayloadRecuperacion([.. lote.Select(m => m.MensajeExternoId)]),
                    lote.Min(m => m.RecibidoUtc)));

            avanzarCursor(conexion);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            listado = new Listado(null, EstadoRecuperacion.De(conexion));
        }, cancellationToken);

        return ejecutado ? listado! : new Listado(ResultadoRecuperacionHueco.Ocupada);
    }

    /// <summary>
    /// Lo que de verdad hay que encolar de una página, leído bajo el cerrojo y después de comprobar la recuperación: fuera
    /// los Ids ya ingeridos en la conexión (el mismo criterio con que la ingesta los descarta) y los que ya están en un
    /// evento de recuperación Pendiente o Procesando, de esta recuperación o de una anulada. Ninguno de los dos saltos
    /// cambia la limpieza: el primero ya está en la aplicación; el segundo se ingerirá o, si su evento se descarta, la marca
    /// retrocede y la recuperación se anula, así que esta pasada no llega a guardar ni a limpiar sobre él.
    /// <para>
    /// Límite declarado (issue #2124, ronda 5 de la revisión): los Ids de un evento de recuperación DescartadoDefinitivo sí
    /// se vuelven a encolar cuando la recuperación vuelve a empezar. Saltarlos permitiría limpiar la marca con un mensaje sin
    /// ingerir, protegido solo por el aviso de correo descartado, así que se prefiere repetir el intento. Con un GET de Graph
    /// que falla siempre, cada reinicio crea un evento por lote de esos Ids. Con el sondeo de 15 minutos son del orden de 96
    /// eventos al día por lote, unos 670 hasta que el mensaje sale del tope de antigüedad de 7 días
    /// (<see cref="ConexionIntegracion.AntiguedadMaximaRecuperacion"/>). Las filas descartadas se redactan, no se borran.
    /// Decisión de Chris del 2026-11-16: se fusiona con este residual.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<MensajeRecibidoGraphDto>> SinYaIngeridosNiEnCursoAsync(
        Guid conexionId, IReadOnlyList<MensajeRecibidoGraphDto> unicos, CancellationToken cancellationToken)
    {
        if (unicos.Count == 0)
            return unicos;

        var ingeridos = await mensajesIngeridos.ObtenerMensajesExternosExistentesAsync(
            conexionId, [.. unicos.Select(m => m.MensajeExternoId)], cancellationToken);
        var enCurso = (await eventoRepositorio.ObtenerPayloadsDeRecuperacionesEnCursoAsync(conexionId, cancellationToken))
            .SelectMany(graphClient.ExtraerMensajeIdsDeNotificacion)
            .ToHashSet(StringComparer.Ordinal);
        return [.. unicos.Where(m => !ingeridos.Contains(m.MensajeExternoId) && !enCurso.Contains(m.MensajeExternoId))];
    }

    /// <summary>
    /// Bajo el cerrojo: guarda el segundo en que la recuperación está detenida, sin encolar nada ni mover el cursor. Si la
    /// fila ya no tiene la recuperación que se estaba siguiendo, no se guarda nada.
    /// </summary>
    private async Task<Listado> RegistrarAtascoAsync(Guid conexionId, EstadoRecuperacion esperada, CancellationToken cancellationToken)
    {
        Listado? listado = null;
        var ejecutado = await cerrojo.IntentarEjecutarAsync(conexionId, async ct =>
        {
            var conexion = await conexionRepositorio.ObtenerActualPorIdAsync(conexionId, ct);
            if (conexion is null || !esperada.Coincide(conexion))
            {
                listado = new Listado(ResultadoRecuperacionHueco.Anulada);
                return;
            }

            if (conexion.RegistrarAtascoRecuperacionHueco())
                await unitOfWork.SaveChangesAsync(CancellationToken.None);
            listado = new Listado(ResultadoRecuperacionHueco.Atascada);
        }, cancellationToken);

        return ejecutado ? listado! : new Listado(ResultadoRecuperacionHueco.Ocupada);
    }

    /// <summary>
    /// Bajo el cerrojo y releyendo de la base: limpia la marca si el listado está completo, la recuperación es la misma,
    /// no queda ningún evento de recuperación sin ingerir y la conexión sigue operativa con la suscripción viva.
    /// </summary>
    private async Task<ResultadoRecuperacionHueco> IntentarLimpiarAsync(
        Guid conexionId, EstadoRecuperacion esperada, CancellationToken cancellationToken)
    {
        var resultado = ResultadoRecuperacionHueco.Ocupada;
        await cerrojo.IntentarEjecutarAsync(conexionId, async ct =>
        {
            var ahoraUtc = DateTime.UtcNow;
            var conexion = await conexionRepositorio.ObtenerActualPorIdAsync(conexionId, ct);
            if (conexion is null || !esperada.Coincide(conexion))
            {
                resultado = ResultadoRecuperacionHueco.Anulada;
                return;
            }

            if (await eventoRepositorio.ExistenRecuperacionesSinIngerirAsync(conexionId, ct))
            {
                resultado = ResultadoRecuperacionHueco.PendienteIngesta;
                return;
            }

            var suscripcion = await suscripcionRepositorio.ObtenerActualPorConexionAsync(conexionId, ct);
            if (!conexion.OperativaVerificada(ahoraUtc) || suscripcion is null || suscripcion.FechaExpiracionUtc <= ahoraUtc)
            {
                resultado = ResultadoRecuperacionHueco.SinRecuperar;
                return;
            }

            if (conexion.RecuperacionHuecoRecorridaPorId)
            {
                logger.LogWarning(
                    "La recuperación del hueco de {ConexionId} terminó recorriendo algún segundo por Id, sin verificar contra Graph real que ese recorrido no salte nada (issue #2263): lo leído quedó encolado y la marca ({Marca}) se queda.",
                    conexionId, conexion.HuecoPendienteDesdeUtc);
                resultado = ResultadoRecuperacionHueco.RecorridaPorIdSinVerificar;
                return;
            }

            if (!conexion.CompletarRecuperacionHueco())
            {
                logger.LogWarning(
                    "La recuperación del hueco de {ConexionId} terminó, pero no cubre la marca entera ({Marca}): la marca se queda.",
                    conexionId, conexion.HuecoPendienteDesdeUtc);
                resultado = ResultadoRecuperacionHueco.Truncada;
                return;
            }

            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            resultado = ResultadoRecuperacionHueco.Limpiada;
        }, cancellationToken);

        return resultado;
    }
}
