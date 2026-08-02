using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Asignaciones.Commands.CrearAsignacion;

public record CrearAsignacionCommand(Guid TrabajadorId, Guid CentroId, DateOnly FechaAlta) : ICommand<Guid>;

public class CrearAsignacionCommandValidator : AbstractValidator<CrearAsignacionCommand>
{
    public CrearAsignacionCommandValidator()
    {
        RuleFor(c => c.TrabajadorId).NotEmpty().WithMessage("Selecciona un trabajador.");
        RuleFor(c => c.CentroId).NotEmpty().WithMessage("Selecciona un centro.");
    }
}

public class CrearAsignacionCommandHandler(
    IAsignacionRepository repositorio, IApplicationDbContext dbContext, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearAsignacionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearAsignacionCommand request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos (P0-1 de docs/business/MATURITY_REVIEW.md):
        // sin esto, un Id de otro tenant se persistía sin error, sellado con
        // el tenant actual — el filtro global ya deja "no encontrado" un Id
        // ajeno, así que basta con consultar dentro del ámbito normal.
        if (!await dbContext.Trabajadores.AnyAsync(t => t.Id == request.TrabajadorId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Asignacion.TrabajadorNoEncontrado", "No encontramos este trabajador."));

        if (!await dbContext.Centros.AnyAsync(c => c.Id == request.CentroId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Asignacion.CentroNoEncontrado", "No encontramos este centro."));

        if (await repositorio.ExisteActivaAsync(request.TrabajadorId, request.CentroId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "Asignacion.YaActiva", "Este trabajador ya está dado de alta en este centro."));

        var asignacion = new Asignacion(request.TrabajadorId, request.CentroId, request.FechaAlta);
        repositorio.Agregar(asignacion);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(asignacion.Id);
    }
}
