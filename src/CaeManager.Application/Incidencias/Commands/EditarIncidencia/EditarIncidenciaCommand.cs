using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Incidencias;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Incidencias.Commands.EditarIncidencia;

public record EditarIncidenciaCommand(
    Guid Id, Guid? TrabajadorId, TipoIncidencia Tipo, GravedadIncidencia Gravedad,
    DateOnly FechaOcurrencia, string Descripcion, Guid Version = default) : ICommand;

public class EditarIncidenciaCommandValidator : AbstractValidator<EditarIncidenciaCommand>
{
    public EditarIncidenciaCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Descripcion)
            .NotEmpty().WithMessage("Describe qué ocurrió.")
            .MaximumLength(Incidencia.LongitudMaximaDescripcion);
    }
}

public class EditarIncidenciaCommandHandler(
    IIncidenciaRepository repositorio, ITrabajadoresQueryContext trabajadoresContext, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarIncidenciaCommand, Result>
{
    public async Task<Result> Handle(EditarIncidenciaCommand request, CancellationToken cancellationToken)
    {
        var incidencia = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (incidencia is null)
            return Result.Fallo(Error.Crear("Incidencia.NoEncontrada", "No encontramos esta incidencia."));

        if (ConcurrenciaOptimista.Verificar(incidencia, request.Version, "esta incidencia") is { } conflicto)
            return Result.Fallo(conflicto);

        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        if (request.TrabajadorId is { } trabajadorId
            && !await trabajadoresContext.Trabajadores.AnyAsync(t => t.Id == trabajadorId, cancellationToken))
            return Result.Fallo(Error.Crear("Incidencia.TrabajadorNoEncontrado", "No encontramos este trabajador."));

        try
        {
            incidencia.Actualizar(request.TrabajadorId, request.Tipo, request.Gravedad, request.FechaOcurrencia, request.Descripcion);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("Incidencia.DatosInvalidos", ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
