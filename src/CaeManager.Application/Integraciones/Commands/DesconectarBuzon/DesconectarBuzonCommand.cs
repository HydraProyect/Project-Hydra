using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones.Commands.DesconectarBuzon;

public record DesconectarBuzonCommand(Guid ConexionId) : ICommand;

public class DesconectarBuzonCommandValidator : AbstractValidator<DesconectarBuzonCommand>
{
    public DesconectarBuzonCommandValidator()
    {
        RuleFor(c => c.ConexionId).NotEmpty();
    }
}

/// <summary>
/// Deshabilita una conexión, libera su reclamación del buzón y borra su suscripción de Graph.
/// <para>
/// En Microsoft 365 todo corre dentro de <see cref="ICerrojoVerificacionBuzon"/>, el mismo cerrojo por conexión de
/// Reactivar y de la verificación de identidad (ciclo diario y reintento): sin él, Reactivar podía entrar entre el
/// guardado de la Deshabilitada y el DELETE de Graph, renovar la suscripción y dejar la conexión Habilitada, y después
/// este borrado se llevaba esa suscripción viva. Si el cerrojo está ocupado no se espera: se pide volver a intentarlo,
/// como Reactivar (<see cref="CodigoVerificacionEnCurso"/>). Ya con el cerrojo, la conexión, su suscripción y su
/// reclamación se releen de la base y todas las decisiones salen de ahí (<see cref="DesconectarBajoCerrojoAsync"/>).
/// La reparación de la marca del hueco por registrar del ciclo diario (<see cref="ReparacionMarcaHuecoService"/>) y la
/// acción Reparar de Conexiones (<see cref="RepararMarcaHueco.RepararMarcaHuecoCommand"/>) también toman este cerrojo.
/// </para>
/// <para>
/// Los demás escritores de la conexión o de su suscripción no toman el cerrojo y les basta la concurrencia optimista de
/// la versión de <see cref="ConexionIntegracion"/>:
/// <list type="bullet">
/// <item>La renovación diaria (<see cref="RenovacionSuscripcionesWebhookService"/>) solo prolonga la expiración de la
/// misma suscripción (Graph renueva sin cambiar el Id). Si la renovó antes del DELETE, el DELETE la borra igual; si después,
/// Graph responde 404 y su guardado choca con la versión que dejó la Deshabilitada: al recargar ve Deshabilitada y no
/// aplica nada. Nunca rehabilita.</item>
/// <item>La ingesta solo retrasa la marca del hueco (nunca la adelanta ni toca estado o suscripción): una colisión le
/// cuesta un intento, no un estado.</item>
/// <item>Conectar crea siempre una conexión nueva, con su propio Id y su propio cerrojo.</item>
/// </list>
/// </para>
/// </summary>
public class DesconectarBuzonCommandHandler(
    IConexionIntegracionRepository conexionRepositorio,
    ISuscripcionWebhookRepository suscripcionRepositorio,
    IReclamacionBuzonIntegracionRepository reclamacionRepositorio,
    IAlcanceDatosService alcanceDatos,
    IMicrosoft365GraphClient graphClient,
    AccesoGraphService accesoGraph,
    IUnitOfWork unitOfWork,
    ICerrojoVerificacionBuzon cerrojo,
    IDescarteCambiosPendientes descarteCambios,
    ReparacionMarcaHuecoService reparacion,
    TimeProvider reloj,
    ILogger<DesconectarBuzonCommandHandler> logger)
    : IRequestHandler<DesconectarBuzonCommand, Result>
{
    /// <summary>El mismo código que devuelve Reactivar con el cerrojo ocupado.</summary>
    public const string CodigoVerificacionEnCurso = ReactivarConexion.ReactivarConexionCommandHandler.CodigoVerificacionEnCurso;

    /// <summary>Guardados de la marca del hueco ante colisiones de concurrencia seguidas, contando el primero.</summary>
    public const int IntentosGuardadoMarca = 3;

    /// <summary>
    /// La desconexión quedó guardada, pero la marca del hueco no llegó a la base ni al reintentarla sobre la versión
    /// recargada: la conexión está Deshabilitada con <see cref="ConexionIntegracion.MarcaHuecoPorRegistrarDesdeUtc"/>
    /// puesta, que Conexiones muestra como «Hueco sin registrar» con la acción Reparar. Sale de la desconexión y también
    /// al repetirla sobre la conexión ya Deshabilitada, si la reparación tampoco llega
    /// (<see cref="RepararMarcaDeDeshabilitadaAsync"/>).
    /// </summary>
    public const string CodigoMarcaHuecoNoGuardada = "ConexionIntegracion.MarcaHuecoNoGuardada";

    public async Task<Result> Handle(DesconectarBuzonCommand request, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(request.ConexionId, cancellationToken);
        if (conexion is null || !await VisibleAsync(conexion, cancellationToken))
            return Result.Fallo(Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));

        if (conexion.Proveedor != ProveedorIntegracion.Microsoft365)
            return await DesconectarAsync(conexion, cancellationToken);

        Result? resultado = null;
        if (!await cerrojo.IntentarEjecutarAsync(conexion.Id, async ct => resultado = await DesconectarBajoCerrojoAsync(conexion.Id, ct), cancellationToken))
            return Result.Fallo(Error.Crear(
                CodigoVerificacionEnCurso, "Ya estamos comprobando este buzón en Microsoft. Inténtalo de nuevo en unos segundos."));

        return resultado!;
    }

    /// <summary>
    /// La conexión es visible para el usuario por su cartera y, además, por sí misma: un buzón personal de OTRO gestor
    /// no se resuelve por cartera (tiene el Cliente a null, igual que el genérico del tenant), mismo hallazgo y mismo
    /// arreglo que EnviarMensajeNuevoCommandHandler.
    /// </summary>
    private Task<bool> VisibleAsync(ConexionIntegracion conexion, CancellationToken cancellationToken) =>
        ConexionVisibleAsync(alcanceDatos, conexion, cancellationToken);

    /// <summary>
    /// La misma comprobación de visibilidad para los comandos que operan sobre una conexión ya cargada (también
    /// <see cref="RepararMarcaHueco.RepararMarcaHuecoCommandHandler"/>): por la cartera y por la propia conexión.
    /// </summary>
    internal static async Task<bool> ConexionVisibleAsync(
        IAlcanceDatosService alcanceDatos, ConexionIntegracion conexion, CancellationToken cancellationToken) =>
        await alcanceDatos.ClienteOpcionalVisibleAsync(conexion.ClienteId, cancellationToken)
        && await alcanceDatos.ConexionIntegracionVisibleAsync(conexion.Id, cancellationToken);

    /// <summary>
    /// Todo sale del estado releído aquí, ya con el cerrojo: la instancia cargada antes de tomarlo puede estar obsoleta
    /// (otro Reactivar o Desconectar terminó entre la carga y el cerrojo), y EF no comprueba la versión de una entidad
    /// que no cambia, así que <c>Deshabilitar()</c> sobre una Deshabilitada obsoleta liberaba la reclamación y borraba la
    /// suscripción que otra operación acababa de crear para una conexión ya Habilitada.
    /// <list type="bullet">
    /// <item>Releída Deshabilitada: quien la desconectó liberó la reclamación en el mismo guardado y borró la suscripción.
    /// No se deshabilita otra vez ni se llama a Graph; la decisión es estable mientras dure el cerrojo, porque solo
    /// Reactivar la saca de Deshabilitada y también lo toma. Lo único que se repara es la marca del hueco que quedó por
    /// registrar en la base (<see cref="ConexionIntegracion.MarcaHuecoPorRegistrarDesdeUtc"/>), por si la desconexión
    /// anterior devolvió <see cref="CodigoMarcaHuecoNoGuardada"/> (<see cref="RepararMarcaDeDeshabilitadaAsync"/>).</item>
    /// <item>Releída Habilitada o ConError: se deshabilita esa versión (siempre cambia el estado, así que el guardado
    /// compara la versión releída y choca con cualquier escritor sin cerrojo posterior, como la renovación), se libera la
    /// reclamación que hay ahora en la base y se borra la suscripción releída.</item>
    /// </list>
    /// </summary>
    private async Task<Result> DesconectarBajoCerrojoAsync(Guid conexionId, CancellationToken cancellationToken)
    {
        var actual = await conexionRepositorio.ObtenerActualPorIdAsync(conexionId, cancellationToken);

        // La visibilidad se vuelve a comprobar sobre el estado releído, como en Reactivar: la de fuera del cerrojo se
        // decidió con la instancia cargada antes.
        if (actual is null || !await VisibleAsync(actual, cancellationToken))
            return Result.Fallo(Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));

        if (actual.Estado == EstadoConexionIntegracion.Deshabilitada)
            return await RepararMarcaDeDeshabilitadaAsync(actual);

        return await DesconectarAsync(actual, cancellationToken);
    }

    /// <summary>
    /// Desconectar otra vez una conexión ya Deshabilitada repara la marca del hueco que la desconexión anterior no pudo
    /// guardar (la acción Reparar de Conexiones no pasa por aquí: es <see cref="RepararMarcaHueco.RepararMarcaHuecoCommand"/>,
    /// que nunca desconecta) (<see cref="CodigoMarcaHuecoNoGuardada"/>). Todo sale de
    /// <see cref="ConexionIntegracion.MarcaHuecoPorRegistrarDesdeUtc"/>, guardada en el mismo guardado que la Deshabilitada:
    /// no depende de que la suscripción siga vigente o haya expirado ni de que llegue una notificación, y registra el
    /// comienzo real del hueco, no la expiración (Codex, ronda 18, hallazgo medio). Sin nada por registrar, devuelve éxito
    /// sin guardar: una segunda desconexión de una conexión desconectada sin problemas no deja aviso. Si la reparación no
    /// llega a la base, el mismo fallo visible (<see cref="ReparacionMarcaHuecoService.RepararBajoCerrojoAsync"/>).
    /// </summary>
    private async Task<Result> RepararMarcaDeDeshabilitadaAsync(ConexionIntegracion conexion)
    {
        if (conexion.MarcaHuecoPorRegistrarDesdeUtc is null || await reparacion.RepararBajoCerrojoAsync(conexion.Id))
            return Result.Exito();

        logger.LogCritical(
            "La conexión {ConexionId} sigue Deshabilitada sin la marca de hueco: no se pudo guardar al repetir la desconexión.",
            conexion.Id);
        return FalloMarcaHuecoNoGuardada();
    }

    private static Result FalloMarcaHuecoNoGuardada() => Result.Fallo(Error.Crear(
        CodigoMarcaHuecoNoGuardada,
        "Hemos desconectado el buzón, pero no hemos podido registrar desde cuándo dejó de recibirse su correo."));

    private async Task<Result> DesconectarAsync(ConexionIntegracion conexion, CancellationToken cancellationToken)
    {
        var suscripcion = await suscripcionRepositorio.ObtenerActualPorConexionAsync(conexion.Id, cancellationToken);

        // El borrado va contra la suscripción que se guarda junto a la Deshabilitada, y solo contra ella: se fija aquí,
        // antes del guardado y dentro del cerrojo, donde nadie la sustituye.
        var graphSubscriptionId = suscripcion?.GraphSubscriptionId;
        var expiracionSuscripcionUtc = suscripcion?.FechaExpiracionUtc;

        // Regla conservadora del hueco sobre la suscripción observada, guardada antes de cualquier llamada externa (el
        // token incluido): si ya expiró o la conexión no está Habilitada, el correo dejó de entrar antes de desconectar,
        // y eso no depende de lo que responda el DELETE. Un 404 cuenta como borrado confirmado (la suscripción expirada
        // ya no existe en Graph), así que sin esto la pérdida entre la expiración y la desconexión no se declaraba
        // (revisor-puente, ronda 13, hallazgo medio). Con la conexión Habilitada y la suscripción vigente no guarda nada.
        // Si choca y recarga, se sigue con la versión recargada; agotados los intentos, la excepción sale sin llamar a
        // nada externo ni deshabilitar, como en la renovación.
        if (graphSubscriptionId is not null)
        {
            var antesDeGraphUtc = AhoraUtc();
            if (await RegistrarMarcaAsync(
                    conexion, c => c.RegistrarHuecoAlObservarSuscripcion(expiracionSuscripcionUtc, antesDeGraphUtc),
                    lanzarSiSeAgota: true, CancellationToken.None)
                is not { } conMarca)
                return Result.Fallo(Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));

            conexion = conMarca;
        }

        // El token se pide antes del guardado de la desconexión: AccesoGraphService no guarda, y el refresh token que
        // rote viaja en ese guardado.
        var accessTokenResultado = graphSubscriptionId is null
            ? null
            : await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion, cancellationToken);

        // La suscripción pudo expirar mientras se pedía el token: la regla se vuelve a aplicar sobre la conexión aún sin
        // deshabilitar y viaja en el mismo guardado de la desconexión, antes del DELETE.
        //
        // En ese mismo guardado queda el comienzo del hueco por registrar (MarcaHuecoPorRegistrarDesdeUtc): desde aquí la
        // ingesta descarta las notificaciones y el resultado del borrado aún no se conoce. Los guardados de después del
        // DELETE lo vacían al registrar la marca (o al confirmar que no había hueco); si ninguno llega a la base, queda
        // guardado y lo reparan Desconectar repetido, Reparar en Conexiones y el ciclo diario (Codex, ronda 18).
        DateTime? huecoPorRegistrarDesdeUtc = null;
        if (graphSubscriptionId is not null)
        {
            // Con la suscripción ya expirada (o la conexión no Habilitada) la marca queda aquí en esa hora o antes y no hay
            // nada más por registrar: Deshabilitar solo guarda un comienzo anterior a la marca.
            var antesDelGuardadoUtc = AhoraUtc();
            conexion.RegistrarHuecoAlObservarSuscripcion(expiracionSuscripcionUtc, antesDelGuardadoUtc);
            huecoPorRegistrarDesdeUtc = antesDelGuardadoUtc;
        }

        // Primero se guarda la desconexión y después se borra en Graph: si el borrado fuera primero y el guardado
        // fallase, la conexión seguiría Habilitada sin suscripción y sin aviso. Deshabilitada nunca pasa a ConError.
        conexion.Deshabilitar(huecoPorRegistrarDesdeUtc);
        // Libera el buzón para cualquier Tenant (incremento 1 de
        // PROPUESTA-BUZONES-COMPARTIDOS-M365 § 5.4) — solo aquí, nunca en la
        // transición automática a ConError: una conexión con error transitorio
        // sigue siendo dueña de su buzón.
        await reclamacionRepositorio.LiberarAsync(conexion.Id, cancellationToken);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Como en Reactivar: la desconexión no se guardó, y la Deshabilitada y la baja de la reclamación rastreadas no
            // deben guardarse con otra operación del circuito. Graph no se ha tocado.
            descarteCambios.DescartarCambiosPendientes();
            throw;
        }

        if (graphSubscriptionId is null)
            return Result.Exito();

        // La desconexión ya está confirmada. El reloj se lee ahora, después de la confirmación, y la regla del hueco se
        // vuelve a aplicar sobre esa lectura antes del DELETE, sin depender de lo que Graph responda: si la suscripción
        // expiró en cualquier momento antes de este punto (también durante el propio guardado, después de la última
        // comprobación), el correo dejó de entrar antes de que la desconexión quedara confirmada, y un 404 posterior del
        // DELETE, contado como borrado con éxito, no lo declararía (Codex, ronda 15, hallazgo medio). Solo se marca si
        // expiró: con la conexión ya Deshabilitada, la regla marcaría también una suscripción vigente. Si la expiración
        // ya quedó marcada antes (antes del token o en el guardado de la desconexión), la marca no cambia y no se guarda
        // nada. Puede avisar de más (una expiración entre la confirmación y esta lectura), nunca de menos.
        //
        // Desde aquí, cada guardado de la marca que no llega a la base se reintenta sobre la versión recargada antes de
        // devolver, también cuando el DELETE tiene éxito: sin ese reintento, un fallo de este guardado seguido de un DELETE
        // con éxito dejaba la conexión Deshabilitada y sin marca, con éxito y sin aviso (Codex, ronda 16, hallazgo medio).
        // vigente es la versión con la que seguir: null si el último guardado de la marca no llegó a la base, y entonces el
        // siguiente intento parte de la base. La instancia del guardado fallido ya lleva la marca en memoria y el descarte
        // la dejó fuera del contexto: reutilizarla haría que el intento siguiente viera la marca como puesta y no guardase
        // nada (revisor-puente, ronda 15, hallazgo medio).
        //
        // Cada uno de estos guardados vacía también MarcaHuecoPorRegistrarDesdeUtc
        // (ConexionIntegracion.ResolverMarcaHuecoPorRegistrar): la marca registrada ya cubre lo que habría que registrar.
        // sinGuardar es la observación cuyo guardado no llegó a la base y se reintenta al final; Registrar dice si esa
        // observación registra la marca o solo vacía el valor por registrar (borrado con éxito y sin expiración).
        ConexionIntegracion? vigente = conexion;
        (DateTime ObservadaUtc, bool Registrar)? sinGuardar = null;
        var confirmadaUtc = AhoraUtc();
        if (expiracionSuscripcionUtc is { } expiracion && expiracion <= confirmadaUtc)
        {
            vigente = await GuardarMarcaTrasDesconexionAsync(vigente, conexion.Id, expiracionSuscripcionUtc, confirmadaUtc, registrarHueco: true);
            if (vigente is null)
                sinGuardar = (confirmadaUtc, true);
        }

        // Best-effort: si Graph no responde, la suscripción expira sola en unos días (la renovación deja de renovarla
        // porque la conexión está deshabilitada) y no bloquea la desconexión, que ya está guardada. Un borrado fallido o
        // de resultado incierto (error, tiempo agotado, excepción, cancelación) deja siempre la marca del hueco en el
        // comienzo guardado con la Deshabilitada: desde ahí el correo de este buzón no entra. Un borrado con éxito (también el 404 de una
        // suscripción ya expirada) no añade nada: la pérdida anterior ya quedó marcada antes del token, en el guardado
        // de la desconexión o justo después de confirmarla.
        string? motivoFallo;
        try
        {
            motivoFallo = await EliminarEnGraphAsync(accessTokenResultado, graphSubscriptionId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            motivoFallo = "cancelado";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "El borrado de la suscripción {SubscriptionId} de {ConexionId} lanzó.", graphSubscriptionId, conexion.Id);
            motivoFallo = ex.GetType().Name;
        }

        if (motivoFallo is not null)
        {
            logger.LogWarning(
                "No se pudo eliminar la suscripción {SubscriptionId} de Graph al desconectar {ConexionId}: {Error}",
                graphSubscriptionId, conexion.Id, motivoFallo);

            // La marca se guarda en cualquier camino del fallo (error de Graph, sin token, excepción o borrado cancelado),
            // y empieza en el comienzo que se guardó por registrar con la Deshabilitada (huecoPorRegistrarDesdeUtc), no en
            // la hora posterior al fallo: desde ese guardado la ingesta descarta las notificaciones, y un DELETE lento no
            // puede retrasar el comienzo del aviso (Codex, ronda 19, hallazgo medio). Se usa el valor local, no el de la
            // conexión: si el guardado de la expiración tras confirmar ya vació MarcaHuecoPorRegistrarDesdeUtc, este
            // comienzo sigue siendo anterior a la expiración. Con la conexión ya Deshabilitada es min(marca, expiración si
            // ya había expirado en ese comienzo, comienzo), así que también cubre la de tras la confirmación si esa no llegó
            // a la base: la pendiente pasa a ser solo esta.
            var observadaUtc = huecoPorRegistrarDesdeUtc ?? AhoraUtc();
            vigente = await GuardarMarcaTrasDesconexionAsync(vigente, conexion.Id, expiracionSuscripcionUtc, observadaUtc, registrarHueco: true);
            sinGuardar = vigente is null ? (observadaUtc, true) : null;
        }
        else if (sinGuardar is null)
        {
            // Borrado confirmado y sin expiración antes de confirmar: no hay hueco, y el valor por registrar se vacía para
            // que nadie lo repare como si lo hubiera. Si ya se vació con la marca de la expiración, no se guarda nada.
            var observadaUtc = AhoraUtc();
            vigente = await GuardarMarcaTrasDesconexionAsync(vigente, conexion.Id, expiracionSuscripcionUtc, observadaUtc, registrarHueco: false);
            if (vigente is null)
                sinGuardar = (observadaUtc, false);
        }

        // Reintento sobre la versión recargada, con la misma observación: la marca solo retrocede, así que reaplicarla es
        // idempotente. Si tampoco llega a la base, no se devuelve un éxito silencioso, sino un fallo que el llamador ve, y
        // prevalece sobre la cancelación pedida durante el borrado, porque la desconexión ya está guardada y lo que falta es
        // justo el aviso del hueco. La base conserva MarcaHuecoPorRegistrarDesdeUtc, que se guardó con la Deshabilitada: la
        // reparación no depende de esta instancia ni de este proceso. Con un borrado confirmado esa reparación puede avisar
        // de más, nunca de menos. Excepción: si la suscripción expiró antes de confirmar, el guardado de la expiración llegó
        // a la base (y vació ese valor) y luego fallan el DELETE y todos los guardados del borrado fallido, la base queda
        // con la marca en la expiración, posterior al comienzo guardado, y sin nada por registrar. El usuario ve el fallo;
        // el desfase no pasa de lo que tardó la confirmación, y se acepta como hueco conocido de este fallo combinado.
        if (sinGuardar is { } pendiente
            && await GuardarMarcaTrasDesconexionAsync(null, conexion.Id, expiracionSuscripcionUtc, pendiente.ObservadaUtc, pendiente.Registrar) is null)
        {
            logger.LogCritical(
                "La conexión {ConexionId} quedó Deshabilitada con la marca de hueco por registrar: no se pudo guardar tras reintentarlo sobre la versión recargada.",
                conexion.Id);
            return FalloMarcaHuecoNoGuardada();
        }

        // La cancelación pedida durante un borrado fallido se propaga después de guardar la marca.
        if (motivoFallo is not null)
            cancellationToken.ThrowIfCancellationRequested();

        return Result.Exito();
    }

    private DateTime AhoraUtc() => reloj.GetUtcNow().UtcDateTime;

    /// <summary>
    /// La desconexión y la liberación de la reclamación ya están guardadas: la marca se guarda sin la cancelación del
    /// llamador. Un fallo de ese guardado (sea cual sea, no solo una colisión) se registra, descarta lo rastreado y no
    /// lanza: el llamador reintenta sobre la versión recargada y, si tampoco llega, devuelve un fallo visible.
    /// </summary>
    /// <param name="conexion">La versión con la que seguir, o null para partir de la de la base (el intento anterior no
    /// llegó a ella).</param>
    /// <param name="registrarHueco">true: registra la marca de la observación; false: solo vacía
    /// <see cref="ConexionIntegracion.MarcaHuecoPorRegistrarDesdeUtc"/>. En los dos casos el valor por registrar se vacía
    /// en el mismo guardado (<see cref="ConexionIntegracion.ResolverMarcaHuecoPorRegistrar"/>).</param>
    /// <returns>La conexión con la que seguir, con la marca ya en la base (o sin nada que marcar); null si la marca no
    /// llegó a la base, también si la conexión ya no está en ella.</returns>
    private async Task<ConexionIntegracion?> GuardarMarcaTrasDesconexionAsync(
        ConexionIntegracion? conexion, Guid conexionId, DateTime? expiracionSuscripcionUtc, DateTime observadaUtc, bool registrarHueco)
    {
        try
        {
            conexion ??= await conexionRepositorio.ObtenerActualPorIdAsync(conexionId, CancellationToken.None);
            if (conexion is not null
                && await RegistrarMarcaAsync(
                        conexion, c => c.ResolverMarcaHuecoPorRegistrar(expiracionSuscripcionUtc, observadaUtc, registrarHueco),
                        lanzarSiSeAgota: false, CancellationToken.None)
                    is { } guardada)
                return guardada;

            // Agotó las colisiones (RegistrarMarcaAsync ya descartó lo pendiente) o la conexión ya no está en la base.
            logger.LogError("No se pudo guardar la marca de hueco de {ConexionId} tras desconectar.", conexionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo guardar la marca de hueco de {ConexionId} tras desconectar.", conexionId);
            descarteCambios.DescartarCambiosPendientes();
        }

        return null;
    }

    /// <returns>null si Graph confirmó el borrado; si no, el motivo.</returns>
    private async Task<string?> EliminarEnGraphAsync(
        Result<string>? accessTokenResultado, string graphSubscriptionId, CancellationToken cancellationToken)
    {
        if (accessTokenResultado is not { EsExitoso: true })
            return accessTokenResultado?.Error.Mensaje ?? "sin token";

        var eliminarResultado = await graphClient.EliminarSuscripcionAsync(accessTokenResultado.Valor, graphSubscriptionId, cancellationToken);
        return eliminarResultado.EsFallido ? eliminarResultado.Error.Mensaje : null;
    }

    /// <summary>
    /// Regla conservadora (<see cref="ConexionIntegracion.RegistrarHuecoAlObservarSuscripcion"/>) sobre la suscripción
    /// observada (<paramref name="aplicar"/>), en tres momentos: antes de cualquier llamada externa, con la conexión aún sin
    /// deshabilitar (marca si la suscripción ya expiró o la conexión no está Habilitada); justo después de confirmar la
    /// desconexión, solo si la suscripción ya había expirado según el reloj leído tras la confirmación (marca en su
    /// expiración); y tras un borrado fallido, con la conexión ya Deshabilitada (disparador b observado en el comienzo
    /// guardado con la Deshabilitada: min(marca, expiración si ya había expirado entonces, ese comienzo)). Tras la confirmación, cada guardado vacía además
    /// <see cref="ConexionIntegracion.MarcaHuecoPorRegistrarDesdeUtc"/>, y un borrado con éxito sin expiración solo la vacía.
    /// La reparación de una conexión ya Deshabilitada va aparte (<see cref="RepararMarcaDeDeshabilitadaAsync"/>). Si el
    /// guardado choca por
    /// concurrencia (la ingesta escribió la conexión entretanto), se descarta lo pendiente, se recarga la conexión y se
    /// vuelve a poner la marca, hasta <see cref="IntentosGuardadoMarca"/> guardados: no se cuenta con que otra
    /// notificación la reponga. La marca solo retrocede, así que reaplicarla sobre la versión recargada es idempotente.
    /// </summary>
    /// <param name="lanzarSiSeAgota">Antes de Graph, true: agotados los intentos la colisión sale y no se llama a nada
    /// externo sin la marca guardada. Tras confirmar la desconexión, false: se registra y el llamador reintenta sobre la
    /// versión recargada (<see cref="GuardarMarcaTrasDesconexionAsync"/>).</param>
    /// <returns>La conexión con la que seguir (la recargada si hubo colisión), o null si al recargar ya no existe o si,
    /// sin lanzar, se agotaron los intentos.</returns>
    private async Task<ConexionIntegracion?> RegistrarMarcaAsync(
        ConexionIntegracion conexion, Func<ConexionIntegracion, bool> aplicar, bool lanzarSiSeAgota,
        CancellationToken cancellationToken)
    {
        var actual = conexion;
        for (var intento = 1; ; intento++)
        {
            if (!aplicar(actual))
                return actual;

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return actual;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                descarteCambios.DescartarCambiosPendientes();
                if (intento >= IntentosGuardadoMarca)
                {
                    logger.LogError(
                        ex, "No se pudo guardar la marca de hueco de {ConexionId} al desconectar: {Intentos} colisiones seguidas.",
                        conexion.Id, intento);
                    if (lanzarSiSeAgota)
                        throw;

                    return null;
                }

                logger.LogWarning(
                    "Colisión de concurrencia al guardar la marca de hueco de {ConexionId} (intento {Intento}): se recarga y se vuelve a poner.",
                    conexion.Id, intento);
                if (await conexionRepositorio.ObtenerActualPorIdAsync(conexion.Id, cancellationToken) is not { } recargada)
                    return null;

                actual = recargada;
            }
            catch when (lanzarSiSeAgota)
            {
                // Antes de Graph, cualquier otro fallo del guardado (base caída, tiempo agotado) tampoco deja la marca
                // rastreada para que la guarde otra operación del circuito, como el catch del guardado de la desconexión.
                // Tras el borrado fallido lo descarta el llamador.
                descarteCambios.DescartarCambiosPendientes();
                throw;
            }
        }
    }
}
