using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Integraciones.Commands.ReactivarConexion;

/// <summary>
/// Vuelve una conexión de <see cref="EstadoConexionIntegracion.ConError"/> (o
/// <see cref="EstadoConexionIntegracion.Deshabilitada"/>) a
/// <see cref="EstadoConexionIntegracion.Habilitada"/> — necesario desde que
/// <c>RenovacionSuscripcionWebhookHostedService</c> puede marcar
/// <c>ConError</c> de verdad (salud de plataforma, A-07): sin esta acción esa
/// conexión no tenía ninguna vía de vuelta salvo SQL directo.
///
/// No reintenta la suscripción de Graph por sí solo — solo limpia el
/// estado/<c>UltimoError</c>; el siguiente ciclo de
/// <c>RenovacionSuscripcionWebhookHostedService</c> se encarga de renovarla
/// si la ventana lo permite. Si la suscripción ya expiró del todo en Graph,
/// reactivar aquí no basta: hace falta reconectar el buzón desde cero.
/// </summary>
public record ReactivarConexionCommand(Guid ConexionId) : ICommand;

public class ReactivarConexionCommandValidator : AbstractValidator<ReactivarConexionCommand>
{
    public ReactivarConexionCommandValidator()
    {
        RuleFor(c => c.ConexionId).NotEmpty();
    }
}

public class ReactivarConexionCommandHandler(
    IConexionIntegracionRepository conexionRepositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<ReactivarConexionCommand, Result>
{
    public async Task<Result> Handle(ReactivarConexionCommand request, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(request.ConexionId, cancellationToken);
        if (conexion is null || !await alcanceDatos.ClienteOpcionalVisibleAsync(conexion.ClienteId, cancellationToken))
            return Result.Fallo(Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));

        conexion.Rehabilitar();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
