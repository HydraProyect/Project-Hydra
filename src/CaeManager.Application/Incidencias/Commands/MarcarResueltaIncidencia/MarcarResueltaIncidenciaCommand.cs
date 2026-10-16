using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Incidencias;
using MediatR;

namespace CaeManager.Application.Incidencias.Commands.MarcarResueltaIncidencia;

public record MarcarResueltaIncidenciaCommand(Guid Id, bool Resuelta) : ICommand;

public class MarcarResueltaIncidenciaCommandHandler(
    IIncidenciaRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<MarcarResueltaIncidenciaCommand, Result>
{
    public async Task<Result> Handle(MarcarResueltaIncidenciaCommand request, CancellationToken cancellationToken)
    {
        var incidencia = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        // Asignación de Cartera: fuera del alcance de gestión de su Centro, la Incidencia «no existe».
        if (incidencia is null || !await alcanceDatos.CentroParaGestionVisibleAsync(incidencia.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("Incidencia.NoEncontrada", "No encontramos esta incidencia."));

        if (request.Resuelta)
            incidencia.MarcarResuelta();
        else
            incidencia.Reabrir();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
