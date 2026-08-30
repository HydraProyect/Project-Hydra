using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Proyectos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Proyectos.Commands.CerrarProyecto;

public record CerrarProyectoCommand(Guid Id, DateOnly FechaCierre, Guid Version = default) : ICommand;

public class CerrarProyectoCommandValidator : AbstractValidator<CerrarProyectoCommand>
{
    public CerrarProyectoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
    }
}

public class CerrarProyectoCommandHandler(IProyectoRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<CerrarProyectoCommand, Result>
{
    public async Task<Result> Handle(CerrarProyectoCommand request, CancellationToken cancellationToken)
    {
        var proyecto = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (proyecto is null || !await ProyectoAutorizacion.VisibleAsync(proyecto.ClienteId, alcanceDatos, cancellationToken))
            return Result.Fallo(Error.Crear("Proyecto.NoEncontrado", "El proyecto no existe o no tienes acceso."));

        if (ConcurrenciaOptimista.Verificar(proyecto, request.Version, "este proyecto") is { } conflicto)
            return Result.Fallo(conflicto);

        if (!proyecto.EstaAbierto)
            return Result.Fallo(Error.Crear("Proyecto.YaCerrado", "Este proyecto ya está cerrado."));

        try
        {
            proyecto.Cerrar(request.FechaCierre);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("Proyecto.FechaCierreInvalida", ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
