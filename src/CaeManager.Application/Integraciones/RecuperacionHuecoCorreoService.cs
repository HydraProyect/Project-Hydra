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

    /// <summary>Lectura completa, todo ingerido, conexión operativa con suscripción viva: la marca se limpió.</summary>
    Limpiada,
}

/// <summary>
/// Recuperación del hueco de correo de Microsoft 365 (issue #2124): lee la Bandeja de entrada desde
/// <see cref="ConexionIntegracion.HuecoPendienteDesdeUtc"/> y limpia la marca solo tras una lectura completa. La llama
/// <c>RecuperacionHuecoCorreoHostedService</c> por Tenant propietario activo, con su ámbito explícito; todo lo que lee y
/// escribe pasa por los repositorios del Tenant de la sesión (filtro de Tenant y RLS).
/// <list type="bullet">
/// <item><b>Orden ascendente con cursor.</b> <c>receivedDateTime ge</c> el cursor de la recuperación
/// (<see cref="ConexionIntegracion.RecuperacionHuecoCursorUtc"/>), en orden ascendente, para que los topes no se gasten
/// en el correo reciente, que ya entra por el webhook. Cada página se encola como un evento de recuperación
/// (<see cref="EventoWebhook.CrearRecuperacion"/>) en el MISMO guardado que avanza el cursor: o quedan los dos o ninguno.</item>
/// <item><b><c>@odata.nextLink</c> hasta el final.</b> Dentro de la pasada se sigue la continuación de Graph, sin
/// fabricar <c>$skip</c>; el listado queda completo solo con una página sin continuación. Un 200 ilegible es un fallo
/// (lo decide el adaptador), y la marca y el cursor se quedan.</item>
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
    ISuscripcionWebhookRepository suscripcionRepositorio,
    IMicrosoft365GraphClient graphClient,
    AccesoGraphService accesoGraph,
    IUnitOfWork unitOfWork,
    ICerrojoVerificacionBuzon cerrojo,
    IDescarteCambiosPendientes descarteCambios,
    ILogger<RecuperacionHuecoCorreoService> logger)
{
    /// <summary>Tope de mensajes encolados por conexión y pasada.</summary>
    public const int MaximoMensajesPorPasada = 250;

    /// <summary>Tope de páginas por conexión y pasada: corta también una continuación que devolviera páginas vacías.</summary>
    public const int MaximoPaginasPorPasada = 20;

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
    private readonly record struct EstadoRecuperacion(DateTime CubreDesdeUtc, DateTime CursorUtc, string? SiguienteEnlace, bool ListadoCompleto)
    {
        public static EstadoRecuperacion De(ConexionIntegracion conexion) =>
            new(conexion.RecuperacionHuecoCubreDesdeUtc!.Value, conexion.RecuperacionHuecoCursorUtc!.Value,
                conexion.RecuperacionHuecoSiguienteEnlace, conexion.RecuperacionHuecoListadoCompleto);

        public bool Coincide(ConexionIntegracion conexion) =>
            conexion.RecuperacionHuecoCubreDesdeUtc == CubreDesdeUtc
            && conexion.RecuperacionHuecoCursorUtc == CursorUtc
            && string.Equals(conexion.RecuperacionHuecoSiguienteEnlace, SiguienteEnlace, StringComparison.Ordinal)
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

    /// <summary>Sigue el listado desde el cursor, página a página, hasta el final o un tope.</summary>
    private async Task<Listado> ListarAsync(
        Guid conexionId, ConexionIntegracion conexion, string accessToken, EstadoRecuperacion recuperacion,
        CancellationToken cancellationToken)
    {
        var buzon = BuzonGraph.De(conexion);
        var desdeUtc = recuperacion.CursorUtc;
        // Una pasada anterior cortada por un tope dejó guardada la continuación: se sigue desde ella.
        var enlace = recuperacion.SiguienteEnlace;
        var mensajes = 0;

        for (var pagina = 1; ; pagina++)
        {
            // La misma puerta que la ingesta, antes de cada llamada a Graph.
            if (!await conexionRepositorio.SigueOperativaVerificadaAsync(conexionId, DateTime.UtcNow, cancellationToken))
            {
                logger.LogWarning("La conexión {ConexionId} dejó de estar operativa durante la recuperación del hueco: se corta.", conexionId);
                return new Listado(ResultadoRecuperacionHueco.Interrumpida);
            }

            var respuesta = await graphClient.ListarRecibidosDesdeAsync(accessToken, buzon, desdeUtc, enlace, cancellationToken);
            if (respuesta.EsFallido)
            {
                logger.LogWarning(
                    "Falló la página {Pagina} de la recuperación del hueco de {ConexionId}: {Error}. La marca y el cursor se quedan.",
                    pagina, conexionId, respuesta.Error.Mensaje);
                // Solo la continuación heredada de la pasada anterior: la de esta pasada ya quedó guardada y, si no vale, se
                // olvidará cuando la pasada siguiente la pida primero.
                if (pagina == 1 && enlace is not null && !CodigosErrorMicrosoft365.EsTransitorio(respuesta.Error))
                    return new Listado(await OlvidarContinuacionAsync(conexionId, recuperacion, cancellationToken));
                return new Listado(ResultadoRecuperacionHueco.Interrumpida);
            }

            var leida = respuesta.Valor;
            var guardada = await GuardarPaginaAsync(conexionId, recuperacion, leida, cancellationToken);
            if (guardada.Resultado is { } corte)
                return new Listado(corte);

            recuperacion = guardada.Recuperacion!.Value;
            mensajes += leida.Mensajes.Count;

            if (leida.SiguienteEnlace is null)
                return new Listado(null, recuperacion);

            if (mensajes >= MaximoMensajesPorPasada || pagina >= MaximoPaginasPorPasada)
            {
                logger.LogWarning(
                    "La recuperación del hueco de {ConexionId} alcanzó el tope de la pasada ({Mensajes} mensajes, {Paginas} páginas): el ciclo siguiente sigue la continuación guardada (cursor {Cursor}).",
                    conexionId, mensajes, pagina, recuperacion.CursorUtc);
                return new Listado(ResultadoRecuperacionHueco.Parcial);
            }

            enlace = leida.SiguienteEnlace;
        }
    }

    /// <summary>
    /// Graph rechazó la continuación guardada (caducada o ya no válida): bajo el cerrojo, si la recuperación sigue siendo
    /// la misma, se olvida y la pasada siguiente vuelve a listar desde el cursor.
    /// </summary>
    private async Task<ResultadoRecuperacionHueco> OlvidarContinuacionAsync(
        Guid conexionId, EstadoRecuperacion esperada, CancellationToken cancellationToken)
    {
        var resultado = ResultadoRecuperacionHueco.Interrumpida;
        await cerrojo.IntentarEjecutarAsync(conexionId, async ct =>
        {
            var conexion = await conexionRepositorio.ObtenerActualPorIdAsync(conexionId, ct);
            if (conexion is null || !esperada.Coincide(conexion))
            {
                resultado = ResultadoRecuperacionHueco.Anulada;
                return;
            }

            if (conexion.OlvidarContinuacionRecuperacionHueco())
                await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }, cancellationToken);

        return resultado;
    }

    /// <summary>
    /// Bajo el cerrojo, en un único guardado: el evento de recuperación de la página y el avance del cursor. Si la fila ya
    /// no tiene la recuperación que se estaba siguiendo, no se guarda nada.
    /// </summary>
    private async Task<Listado> GuardarPaginaAsync(
        Guid conexionId, EstadoRecuperacion esperada, PaginaMensajesRecibidosGraphDto pagina, CancellationToken cancellationToken)
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

            if (pagina.Mensajes.Count > 0)
            {
                var ids = pagina.Mensajes.Select(m => m.MensajeExternoId).Distinct(StringComparer.Ordinal).ToList();
                eventoRepositorio.Agregar(EventoWebhook.CrearRecuperacion(
                    conexionId, graphClient.ConstruirPayloadRecuperacion(ids), pagina.Mensajes.Min(m => m.RecibidoUtc)));
            }

            conexion.AvanzarRecuperacionHueco(
                pagina.Mensajes.Count > 0 ? pagina.Mensajes.Max(m => m.RecibidoUtc) : null, pagina.SiguienteEnlace);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            listado = new Listado(null, EstadoRecuperacion.De(conexion));
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
