using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Proyectos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Proyectos.Commands.CerrarProyecto;

public record CerrarProyectoCommand(Guid Id, DateOnly FechaCierre) : IRequest<Result>;

public class CerrarProyectoCommandValidator : AbstractValidator<CerrarProyectoCommand>
{
    public CerrarProyectoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
    }
}

public class CerrarProyectoCommandHandler(IProyectoRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<CerrarProyectoCommand, Result>
{
    public async Task<Result> Handle(CerrarProyectoCommand request, CancellationToken cancellationToken)
    {
        var proyecto = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (proyecto is null)
            return Result.Fallo(Error.Crear("Proyecto.NoEncontrado", "El proyecto no existe o no tienes acceso."));

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
