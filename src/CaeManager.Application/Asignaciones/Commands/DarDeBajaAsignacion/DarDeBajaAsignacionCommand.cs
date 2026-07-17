using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignacion;

/// <summary>
/// Asignacion no tiene soft delete (ver Domain): darla de baja significa
/// fijar FechaBaja, no eliminarla — así se conserva el historial completo de
/// dónde ha trabajado cada persona (mejora directa sobre la matriz de "X"
/// del Excel original, que no guardaba fechas).
/// </summary>
public record DarDeBajaAsignacionCommand(Guid Id, DateOnly FechaBaja) : IRequest<Result>;

public class DarDeBajaAsignacionCommandValidator : AbstractValidator<DarDeBajaAsignacionCommand>
{
    public DarDeBajaAsignacionCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
    }
}

public class DarDeBajaAsignacionCommandHandler(IAsignacionRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<DarDeBajaAsignacionCommand, Result>
{
    public async Task<Result> Handle(DarDeBajaAsignacionCommand request, CancellationToken cancellationToken)
    {
        var asignacion = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (asignacion is null)
            return Result.Fallo(Error.Crear("Asignacion.NoEncontrada", "No encontramos esta asignación."));

        try
        {
            asignacion.DarDeBaja(request.FechaBaja);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("Asignacion.FechaBajaInvalida", ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
