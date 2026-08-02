using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Proyectos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Proyectos.Commands.DesasignarTecnicoProyecto;

public record DesasignarTecnicoProyectoCommand(Guid Id, DateOnly FechaBaja) : ICommand;

public class DesasignarTecnicoProyectoCommandValidator : AbstractValidator<DesasignarTecnicoProyectoCommand>
{
    public DesasignarTecnicoProyectoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
    }
}

public class DesasignarTecnicoProyectoCommandHandler(IProyectoTecnicoRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<DesasignarTecnicoProyectoCommand, Result>
{
    public async Task<Result> Handle(DesasignarTecnicoProyectoCommand request, CancellationToken cancellationToken)
    {
        var proyectoTecnico = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (proyectoTecnico is null)
            return Result.Fallo(Error.Crear("Proyecto.TecnicoNoEncontrado", "No encontramos esta asignación de técnico."));

        try
        {
            proyectoTecnico.DarDeBaja(request.FechaBaja);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("Proyecto.FechaBajaInvalida", ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
