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
public record DarDeBajaAsignacionCommand(Guid Id, DateOnly FechaBaja) : ICommand;

public class DarDeBajaAsignacionCommandValidator : AbstractValidator<DarDeBajaAsignacionCommand>
{
    public DarDeBajaAsignacionCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
    }
}

public class DarDeBajaAsignacionCommandHandler(
    IAsignacionRepository repositorio, IAutoridadAsignacionesService autoridad, IUnitOfWork unitOfWork)
    : IRequestHandler<DarDeBajaAsignacionCommand, Result>
{
    public async Task<Result> Handle(DarDeBajaAsignacionCommand request, CancellationToken cancellationToken)
    {
        var asignacion = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (asignacion is null)
            return Result.Fallo(Error.Crear("Asignacion.NoEncontrada", "No encontramos esta asignación."));

        // Dar de baja exige la misma autoridad que dar de alta (decision del
        // propietario, 2026-08-29): "la baja no es una excepcion por ser
        // reversible". Antes se cargaba por identificador y se daba de baja sin
        // comprobar nada, asi que un gestor podia retirar a un trabajador del
        // centro de otro conociendo el Id de la asignacion.
        //
        // Se comprueba sobre el CENTRO de la asignacion, no sobre quien la
        // creo: un CoordinadorCae tiene autoridad sobre las asignaciones de los
        // gestores a su cargo, y "es mia" habria dejado fuera ese caso.
        if (!await autoridad.PuedeModificarAsignacionesDelCentroAsync(asignacion.CentroId, cancellationToken))
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
