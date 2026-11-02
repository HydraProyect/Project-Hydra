using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Integraciones.Commands.FijarResponsableBuzon;

/// <summary>Null retira al responsable: las conversaciones nuevas del buzón nacerán sin asignar.</summary>
public record FijarResponsableBuzonCommand(Guid ConexionId, Guid? ResponsableUsuarioId) : ICommand;

public class FijarResponsableBuzonCommandValidator : AbstractValidator<FijarResponsableBuzonCommand>
{
    public FijarResponsableBuzonCommandValidator()
    {
        RuleFor(c => c.ConexionId).NotEmpty();
        RuleFor(c => c.ResponsableUsuarioId).NotEqual(Guid.Empty);
    }
}

/// <summary>
/// Cambiar el responsable solo afecta a las conversaciones nuevas. El rol se mira en el handler y no solo en la
/// página: la pantalla no es una frontera de autorización. El responsable se revalida contra el directorio por
/// el mismo motivo que en <c>AsignarEjecutivoConversacionCommandHandler</c> (hallazgo N-10).
/// </summary>
public class FijarResponsableBuzonCommandHandler(
    IConexionIntegracionRepository conexionRepositorio,
    IAlcanceDatosService alcanceDatos,
    IDirectorioDestinosCartera directorioDestinos,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<FijarResponsableBuzonCommand, Result>
{
    private const string RolAdministrador = "Administrador";

    public async Task<Result> Handle(FijarResponsableBuzonCommand request, CancellationToken cancellationToken)
    {
        if (await currentUserService.ObtenerRolEfectivoAsync() != RolAdministrador)
            return Result.Fallo(Error.Crear(
                "Integraciones.Buzon.SoloAdministrador", "Solo el Administrador puede cambiar el responsable del buzón."));

        var conexion = await conexionRepositorio.ObtenerPorIdAsync(request.ConexionId, cancellationToken);
        if (conexion is null
            || conexion.Proveedor != ProveedorIntegracion.Microsoft365
            || !await alcanceDatos.ClienteOpcionalVisibleAsync(conexion.ClienteId, cancellationToken)
            || !await alcanceDatos.ConexionIntegracionVisibleAsync(conexion.Id, cancellationToken))
            return Result.Fallo(Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));

        if (conexion.GestorPropietarioId is not null)
            return Result.Fallo(Error.Crear(
                "Integraciones.Buzon.PersonalSinResponsable", "Un buzón personal responde siempre a su propietario."));

        if (request.ResponsableUsuarioId is { } responsableId
            && await directorioDestinos.ObtenerAsync(responsableId, cancellationToken) is not { Activa: true })
            return Result.Fallo(Error.Crear(
                "Integraciones.Buzon.ResponsableNoValido", "No encontramos a ese usuario activo en tu organización."));

        conexion.FijarResponsable(request.ResponsableUsuarioId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
