using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Gestiones;
using MediatR;

namespace CaeManager.Application.Gestiones.Commands.CompletarGestion;

public record CompletarGestionCommand(Guid Id, bool Completada) : ICommand;

public class CompletarGestionCommandHandler(
    IGestionRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<CompletarGestionCommand, Result>
{
    public async Task<Result> Handle(CompletarGestionCommand request, CancellationToken cancellationToken)
    {
        var gestion = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (gestion is null || !await alcanceDatos.CentroVisibleAsync(gestion.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("Gestion.NoEncontrada", "No encontramos esta gestión o no tienes acceso."));

        if (request.Completada) gestion.Completar();
        else gestion.Reabrir();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
