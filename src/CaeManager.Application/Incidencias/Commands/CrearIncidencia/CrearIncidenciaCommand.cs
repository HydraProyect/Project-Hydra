using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Incidencias;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Incidencias.Commands.CrearIncidencia;

public record CrearIncidenciaCommand(
    Guid CentroId, Guid? TrabajadorId, TipoIncidencia Tipo, GravedadIncidencia Gravedad,
    DateOnly FechaOcurrencia, string Descripcion) : IRequest<Result<Guid>>;

public class CrearIncidenciaCommandValidator : AbstractValidator<CrearIncidenciaCommand>
{
    public CrearIncidenciaCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty().WithMessage("Selecciona un centro.");
        RuleFor(c => c.Descripcion)
            .NotEmpty().WithMessage("Describe qué ocurrió.")
            .MaximumLength(Incidencia.LongitudMaximaDescripcion);
    }
}

public class CrearIncidenciaCommandHandler(
    IIncidenciaRepository repositorio, IApplicationDbContext dbContext, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearIncidenciaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearIncidenciaCommand request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        if (!await dbContext.Centros.AnyAsync(c => c.Id == request.CentroId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Incidencia.CentroNoEncontrado", "No encontramos este centro."));

        if (request.TrabajadorId is { } trabajadorId
            && !await dbContext.Trabajadores.AnyAsync(t => t.Id == trabajadorId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Incidencia.TrabajadorNoEncontrado", "No encontramos este trabajador."));

        var incidencia = new Incidencia(
            request.CentroId, request.TrabajadorId, request.Tipo, request.Gravedad,
            request.FechaOcurrencia, request.Descripcion);

        repositorio.Agregar(incidencia);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(incidencia.Id);
    }
}
