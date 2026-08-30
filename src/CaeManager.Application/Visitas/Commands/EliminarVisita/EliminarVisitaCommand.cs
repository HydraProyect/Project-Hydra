using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using MediatR;

namespace CaeManager.Application.Visitas.Commands.EliminarVisita;

public record EliminarVisitaCommand(Guid Id) : ICommand;

public class EliminarVisitaCommandHandler(
    IVisitaRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService currentUserService)
    : IRequestHandler<EliminarVisitaCommand, Result>
{
    public async Task<Result> Handle(EliminarVisitaCommand request, CancellationToken cancellationToken)
    {
        var visita = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (visita is null)
            return Result.Fallo(Error.Crear("Visita.NoEncontrada", "No encontramos esta visita."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Visita.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        visita.MarcarComoEliminado(usuarioId.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
