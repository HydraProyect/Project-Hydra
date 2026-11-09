using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;
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

public class DesconectarBuzonCommandHandler(
    IConexionIntegracionRepository conexionRepositorio,
    ISuscripcionWebhookRepository suscripcionRepositorio,
    IReclamacionBuzonIntegracionRepository reclamacionRepositorio,
    IAlcanceDatosService alcanceDatos,
    IMicrosoft365GraphClient graphClient,
    AccesoGraphService accesoGraph,
    IUnitOfWork unitOfWork,
    ILogger<DesconectarBuzonCommandHandler> logger)
    : IRequestHandler<DesconectarBuzonCommand, Result>
{
    public async Task<Result> Handle(DesconectarBuzonCommand request, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(request.ConexionId, cancellationToken);
        if (conexion is null || !await alcanceDatos.ClienteOpcionalVisibleAsync(conexion.ClienteId, cancellationToken))
            return Result.Fallo(Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));

        // Un buzón personal de OTRO gestor no se resuelve por cartera de
        // Cliente (tiene ClienteId null, igual que el genérico del tenant) —
        // mismo hallazgo y mismo fix que EnviarMensajeNuevoCommandHandler.
        if (!await alcanceDatos.ConexionIntegracionVisibleAsync(conexion.Id, cancellationToken))
            return Result.Fallo(Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));

        var suscripcion = await suscripcionRepositorio.ObtenerPorConexionAsync(conexion.Id, cancellationToken);
        // El token se pide antes del primer guardado: AccesoGraphService no guarda, y el refresh token que rote viaja
        // en ese guardado.
        var accessTokenResultado = suscripcion is null
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
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (suscripcion is null)
            return Result.Exito();

        // Best-effort: si Graph no responde, la suscripción expira sola en unos días (la renovación deja de renovarla
        // porque la conexión está deshabilitada) y no bloquea la desconexión, que ya está guardada. La marca del hueco
        // queda en el momento del borrado: desde ahí el correo de este buzón no entra.
        string? motivoFallo = null;
        if (accessTokenResultado is { EsExitoso: true })
        {
            var eliminarResultado = await graphClient.EliminarSuscripcionAsync(
                accessTokenResultado.Valor, suscripcion.GraphSubscriptionId, cancellationToken);
            if (eliminarResultado.EsFallido)
                motivoFallo = eliminarResultado.Error.Mensaje;
        }
        else
        {
            motivoFallo = accessTokenResultado?.Error.Mensaje ?? "sin token";
        }

        if (motivoFallo is null)
            return Result.Exito();

        logger.LogWarning(
            "No se pudo eliminar la suscripción {SubscriptionId} de Graph al desconectar {ConexionId}: {Error}",
            suscripcion.GraphSubscriptionId, conexion.Id, motivoFallo);
        var ahora = DateTime.UtcNow;
        if (conexion.RegistrarPerdidaSuscripcion(ahora, ahora))
        {
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex.GetType().Name == "DbUpdateConcurrencyException")
            {
                // Otro proceso tocó la conexión entre los dos guardados: la desconexión ya quedó guardada; solo se
                // pierde la marca, que el siguiente descarte de notificación vuelve a poner.
                logger.LogWarning(ex, "No se pudo guardar la marca de hueco de {ConexionId} tras desconectar.", conexion.Id);
            }
        }

        return Result.Exito();
    }
}
