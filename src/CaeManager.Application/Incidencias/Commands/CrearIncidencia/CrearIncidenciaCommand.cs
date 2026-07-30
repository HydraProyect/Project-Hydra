using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Incidencias;
using FluentValidation;
using MediatR;

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

public class CrearIncidenciaCommandHandler(IIncidenciaRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearIncidenciaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearIncidenciaCommand request, CancellationToken cancellationToken)
    {
        var incidencia = new Incidencia(
            request.CentroId, request.TrabajadorId, request.Tipo, request.Gravedad,
            request.FechaOcurrencia, request.Descripcion);

        repositorio.Agregar(incidencia);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(incidencia.Id);
    }
}
