using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using MediatR;

namespace CaeManager.Application.Visitas.Commands.EliminarVisita;

public record EliminarVisitaCommand(Guid Id, Guid UsuarioId) : ICommand;

public class EliminarVisitaCommandHandler(IVisitaRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarVisitaCommand, Result>
{
    public async Task<Result> Handle(EliminarVisitaCommand request, CancellationToken cancellationToken)
    {
        var visita = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (visita is null)
            return Result.Fallo(Error.Crear("Visita.NoEncontrada", "No encontramos esta visita."));

        visita.MarcarComoEliminado(request.UsuarioId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
