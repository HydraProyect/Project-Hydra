using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Integraciones.Commands.FijarAcuseAutomaticoBuzon;

/// <summary>Activa o desactiva el acuse automático de un buzón compartido; solo afecta a los correos que entren después.</summary>
public record FijarAcuseAutomaticoBuzonCommand(Guid ConexionId, bool Activo) : ICommand;

public class FijarAcuseAutomaticoBuzonCommandValidator : AbstractValidator<FijarAcuseAutomaticoBuzonCommand>
{
    public FijarAcuseAutomaticoBuzonCommandValidator()
    {
        RuleFor(c => c.ConexionId).NotEmpty();
    }
}

/// <summary>
/// Misma autorización que <c>FijarResponsableBuzonCommandHandler</c>: Administrador y buzón visible. El cambio queda en
/// la auditoría por el interceptor, como cualquier modificación de <see cref="ConexionIntegracion"/>. El buzón personal
/// no admite acuse: saldría del buzón de una persona por decisión de otra.
/// </summary>
public class FijarAcuseAutomaticoBuzonCommandHandler(
    IConexionIntegracionRepository conexionRepositorio,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<FijarAcuseAutomaticoBuzonCommand, Result>
{
    private const string RolAdministrador = "Administrador";

    public async Task<Result> Handle(FijarAcuseAutomaticoBuzonCommand request, CancellationToken cancellationToken)
    {
        if (await currentUserService.ObtenerRolEfectivoAsync() != RolAdministrador)
            return Result.Fallo(Error.Crear(
                "Integraciones.Buzon.SoloAdministrador", "Solo el Administrador puede cambiar el acuse automático del buzón."));

        var conexion = await conexionRepositorio.ObtenerPorIdAsync(request.ConexionId, cancellationToken);
        if (conexion is null
            || conexion.Proveedor != ProveedorIntegracion.Microsoft365
            || !await alcanceDatos.ClienteOpcionalVisibleAsync(conexion.ClienteId, cancellationToken)
            || !await alcanceDatos.ConexionIntegracionVisibleAsync(conexion.Id, cancellationToken))
            return Result.Fallo(Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));

        if (conexion.GestorPropietarioId is not null)
            return Result.Fallo(Error.Crear(
                "Integraciones.Buzon.PersonalSinAcuse", "Un buzón personal no envía acuses automáticos."));

        conexion.FijarAcuseAutomatico(request.Activo);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
