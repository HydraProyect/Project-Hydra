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

        var suscripcion = await suscripcionRepositorio.ObtenerPorConexionAsync(conexion.Id, cancellationToken);
        if (suscripcion is not null)
        {
            // Best-effort: si Graph no responde, la suscripción expira sola
            // en unos días (ver RenovacionSuscripcionWebhookHostedService,
            // que deja de renovarla porque la conexión ya está
            // deshabilitada) — no bloquea la desconexión local.
            var accessTokenResultado = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion.Id, cancellationToken);
            if (accessTokenResultado.EsExitoso)
            {
                var eliminarResultado = await graphClient.EliminarSuscripcionAsync(
                    accessTokenResultado.Valor, suscripcion.GraphSubscriptionId, cancellationToken);
                if (eliminarResultado.EsFallido)
                    logger.LogWarning(
                        "No se pudo eliminar la suscripción {SubscriptionId} de Graph al desconectar {ConexionId}: {Error}",
                        suscripcion.GraphSubscriptionId, conexion.Id, eliminarResultado.Error.Mensaje);
            }
        }

        conexion.Deshabilitar();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
