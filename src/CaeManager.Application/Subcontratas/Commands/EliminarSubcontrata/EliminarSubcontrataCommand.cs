using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Subcontratas;
using MediatR;

namespace CaeManager.Application.Subcontratas.Commands.EliminarSubcontrata;

public record EliminarSubcontrataCommand(Guid Id, Guid UsuarioId) : IRequest<Result>;

public class EliminarSubcontrataCommandHandler(ISubcontrataRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarSubcontrataCommand, Result>
{
    public async Task<Result> Handle(EliminarSubcontrataCommand request, CancellationToken cancellationToken)
    {
        var subcontrata = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (subcontrata is null)
            return Result.Fallo(Error.Crear("Subcontrata.NoEncontrada", "No encontramos esta subcontrata."));

        if (await repositorio.TieneTrabajadoresAsync(request.Id, cancellationToken))
            return Result.Fallo(Error.Crear(
                "Subcontrata.TieneTrabajadores",
                "No puedes eliminar una subcontrata con trabajadores. Da de baja a sus trabajadores primero."));

        subcontrata.MarcarComoEliminado(request.UsuarioId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
