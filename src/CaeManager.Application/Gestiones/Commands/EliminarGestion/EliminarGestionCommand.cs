using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Gestiones;
using MediatR;

namespace CaeManager.Application.Gestiones.Commands.EliminarGestion;

public record EliminarGestionCommand(Guid Id) : ICommand;

public class EliminarGestionCommandHandler(
    IGestionRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService)
    : IRequestHandler<EliminarGestionCommand, Result>
{
    public async Task<Result> Handle(EliminarGestionCommand request, CancellationToken cancellationToken)
    {
        var gestion = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (gestion is null || !await alcanceDatos.CentroVisibleAsync(gestion.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("Gestion.NoEncontrada", "No encontramos esta gestión o no tienes acceso."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Gestion.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        gestion.MarcarComoEliminado(usuarioId.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
