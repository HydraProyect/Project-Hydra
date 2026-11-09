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
    ILogger<DesconectarBuzonCommandHandler> logger)
    : IRequestHandler<DesconectarBuzonCommand, Result>
{
    /// <summary>El mismo código que devuelve Reactivar con el cerrojo ocupado.</summary>
    public const string CodigoVerificacionEnCurso = ReactivarConexion.ReactivarConexionCommandHandler.CodigoVerificacionEnCurso;

    /// <summary>Guardados de la marca del hueco ante colisiones de concurrencia seguidas, contando el primero.</summary>
    public const int IntentosGuardadoMarca = 3;

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
    private async Task<bool> VisibleAsync(ConexionIntegracion conexion, CancellationToken cancellationToken) =>
        await alcanceDatos.ClienteOpcionalVisibleAsync(conexion.ClienteId, cancellationToken)
        && await alcanceDatos.ConexionIntegracionVisibleAsync(conexion.Id, cancellationToken);

    /// <summary>
    /// Todo sale del estado releído aquí, ya con el cerrojo: la instancia cargada antes de tomarlo puede estar obsoleta
    /// (otro Reactivar o Desconectar terminó entre la carga y el cerrojo), y EF no comprueba la versión de una entidad
    /// que no cambia, así que <c>Deshabilitar()</c> sobre una Deshabilitada obsoleta liberaba la reclamación y borraba la
    /// suscripción que otra operación acababa de crear para una conexión ya Habilitada.
    /// <list type="bullet">
    /// <item>Releída Deshabilitada: no queda nada que hacer. Quien la desconectó liberó la reclamación en el mismo guardado
    /// y borró la suscripción (o dejó la marca si no pudo). No se guarda ni se llama a Graph; la decisión es estable
    /// mientras dure el cerrojo, porque solo Reactivar la saca de Deshabilitada y también lo toma.</item>
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
            return Result.Exito();

        return await DesconectarAsync(actual, cancellationToken);
    }

    private async Task<Result> DesconectarAsync(ConexionIntegracion conexion, CancellationToken cancellationToken)
    {
        var suscripcion = await suscripcionRepositorio.ObtenerActualPorConexionAsync(conexion.Id, cancellationToken);

        // El borrado va contra la suscripción que se guarda junto a la Deshabilitada, y solo contra ella: se fija aquí,
        // antes del guardado y dentro del cerrojo, donde nadie la sustituye.
        var graphSubscriptionId = suscripcion?.GraphSubscriptionId;
        var expiracionSuscripcionUtc = suscripcion?.FechaExpiracionUtc;

        // El token se pide antes del primer guardado: AccesoGraphService no guarda, y el refresh token que rote viaja
        // en ese guardado.
        var accessTokenResultado = graphSubscriptionId is null
            ? null
            : await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion, cancellationToken);

        // Primero se guarda la desconexión y después se borra en Graph: si el borrado fuera primero y el guardado
        // fallase, la conexión seguiría Habilitada sin suscripción y sin aviso. Deshabilitada nunca pasa a ConError.
        conexion.Deshabilitar();
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

        // Best-effort: si Graph no responde, la suscripción expira sola en unos días (la renovación deja de renovarla
        // porque la conexión está deshabilitada) y no bloquea la desconexión, que ya está guardada. Un borrado fallido o
        // de resultado incierto (error, tiempo agotado, excepción, cancelación) deja siempre la marca del hueco en el
        // momento del borrado: desde ahí el correo de este buzón no entra.
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

        if (motivoFallo is null)
            return Result.Exito();

        logger.LogWarning(
            "No se pudo eliminar la suscripción {SubscriptionId} de Graph al desconectar {ConexionId}: {Error}",
            graphSubscriptionId, conexion.Id, motivoFallo);

        // La desconexión y la liberación de la reclamación ya están guardadas: la marca se guarda siempre sin la
        // cancelación del llamador, en cualquier camino del fallo (error de Graph, sin token, excepción o borrado
        // cancelado), y la cancelación pedida se propaga después. Un fallo de ese guardado (sea cual sea, no solo una
        // colisión) se registra, descarta lo rastreado y no tapa el resultado ya confirmado: la conexión está
        // Deshabilitada y su reclamación liberada.
        try
        {
            await RegistrarMarcaAsync(conexion, expiracionSuscripcionUtc, DateTime.UtcNow, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo guardar la marca de hueco de {ConexionId} tras desconectar.", conexion.Id);
            descarteCambios.DescartarCambiosPendientes();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Result.Exito();
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
    /// Disparador (b) en el momento del borrado fallido, con la regla conservadora sobre la conexión ya Deshabilitada:
    /// min(marca, expiración de la suscripción que no se pudo borrar, ahora). Si el guardado choca por concurrencia (la
    /// ingesta escribió la conexión entre los dos guardados), se descarta lo pendiente, se recarga la conexión y se
    /// vuelve a poner la marca, hasta <see cref="IntentosGuardadoMarca"/> guardados: no se cuenta con que otra
    /// notificación la reponga. La marca solo retrocede, así que reaplicarla sobre la versión recargada es idempotente.
    /// </summary>
    private async Task RegistrarMarcaAsync(
        ConexionIntegracion conexion, DateTime? expiracionSuscripcionUtc, DateTime ahoraUtc, CancellationToken cancellationToken)
    {
        var actual = conexion;
        for (var intento = 1; ; intento++)
        {
            if (!actual.RegistrarHuecoAlObservarSuscripcion(expiracionSuscripcionUtc, ahoraUtc))
                return;

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                descarteCambios.DescartarCambiosPendientes();
                if (intento >= IntentosGuardadoMarca)
                {
                    logger.LogError(
                        ex, "No se pudo guardar la marca de hueco de {ConexionId} tras desconectar: {Intentos} colisiones seguidas.",
                        conexion.Id, intento);
                    return;
                }

                logger.LogWarning(
                    "Colisión de concurrencia al guardar la marca de hueco de {ConexionId} (intento {Intento}): se recarga y se vuelve a poner.",
                    conexion.Id, intento);
                if (await conexionRepositorio.ObtenerActualPorIdAsync(conexion.Id, cancellationToken) is not { } recargada)
                    return;

                actual = recargada;
            }
        }
    }
}
