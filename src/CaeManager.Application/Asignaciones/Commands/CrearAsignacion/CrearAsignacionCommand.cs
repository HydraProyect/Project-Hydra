using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Asignaciones.Commands.CrearAsignacion;

public record CrearAsignacionCommand(Guid TrabajadorId, Guid CentroId, DateOnly FechaAlta) : IRequest<Result<Guid>>;

public class CrearAsignacionCommandValidator : AbstractValidator<CrearAsignacionCommand>
{
    public CrearAsignacionCommandValidator()
    {
        RuleFor(c => c.TrabajadorId).NotEmpty().WithMessage("Selecciona un trabajador.");
        RuleFor(c => c.CentroId).NotEmpty().WithMessage("Selecciona un centro.");
    }
}

public class CrearAsignacionCommandHandler(IAsignacionRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearAsignacionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearAsignacionCommand request, CancellationToken cancellationToken)
    {
        if (await repositorio.ExisteActivaAsync(request.TrabajadorId, request.CentroId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "Asignacion.YaActiva", "Este trabajador ya está dado de alta en este centro."));

        var asignacion = new Asignacion(request.TrabajadorId, request.CentroId, request.FechaAlta);
        repositorio.Agregar(asignacion);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(asignacion.Id);
    }
}
