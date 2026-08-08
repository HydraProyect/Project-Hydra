using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento;
using MediatR;

namespace CaeManager.Application.Cumplimiento.Commands.AceptarTerminos;

/// <summary>
/// Registra que el usuario autenticado actual acepta la versión vigente de
/// los Términos y Condiciones + Política de Privacidad
/// (<see cref="VersionTerminos.Actual"/>). Sin parámetros: la versión y el
/// usuario los resuelve el propio handler — nada que el cliente pueda
/// falsear (aceptar "otra versión" no tendría sentido de negocio).
/// </summary>
public record AceptarTerminosCommand : ICommand;

public class AceptarTerminosCommandHandler(
    IAceptacionTerminosRepository repositorio, ICurrentUserService currentUserService, IUnitOfWork unitOfWork)
    : IRequestHandler<AceptarTerminosCommand, Result>
{
    public async Task<Result> Handle(AceptarTerminosCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("AceptacionTerminos.SinUsuario", "No pudimos identificar tu usuario."));

        if (await repositorio.ExisteParaVersionAsync(usuarioId.Value, VersionTerminos.Actual, cancellationToken))
            return Result.Exito();

        repositorio.Agregar(new AceptacionTerminos(usuarioId.Value, VersionTerminos.Actual, DateTime.UtcNow));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
