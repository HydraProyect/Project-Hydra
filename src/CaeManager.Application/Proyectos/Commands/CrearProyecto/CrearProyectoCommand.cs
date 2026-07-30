using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Proyectos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Proyectos.Commands.CrearProyecto;

public record CrearProyectoCommand(
    Guid ClienteId, Guid CentroId, string Nombre, DateOnly FechaInicio, DateOnly? FechaFinPrevista, string? Notas)
    : IRequest<Result<Guid>>;

public class CrearProyectoCommandValidator : AbstractValidator<CrearProyectoCommand>
{
    public CrearProyectoCommandValidator()
    {
        RuleFor(c => c.ClienteId).NotEmpty().WithMessage("Selecciona un cliente.");
        RuleFor(c => c.CentroId).NotEmpty().WithMessage("Selecciona un centro.");
        RuleFor(c => c.Nombre).NotEmpty().MaximumLength(Proyecto.LongitudMaximaNombre);
        RuleFor(c => c.Notas).MaximumLength(Proyecto.LongitudMaximaNotas);
    }
}

public class CrearProyectoCommandHandler(
    IProyectoRepository repositorio, IApplicationDbContext dbContext, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearProyectoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearProyectoCommand request, CancellationToken cancellationToken)
    {
        var centro = await dbContext.Centros
            .Where(c => c.Id == request.CentroId)
            .Select(c => new { c.ClienteId })
            .FirstOrDefaultAsync(cancellationToken);

        if (centro is null)
            return Result.Fallo<Guid>(Error.Crear("Proyecto.CentroNoEncontrado", "No encontramos este centro."));

        if (centro.ClienteId != request.ClienteId)
            return Result.Fallo<Guid>(Error.Crear("Proyecto.CentroNoPerteneceACliente", "Este centro no pertenece al cliente seleccionado."));

        if (await repositorio.ExisteNombreParaClienteAsync(request.ClienteId, request.Nombre, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Proyecto.NombreDuplicado", "Ya existe un proyecto con este nombre para el cliente seleccionado."));

        var proyecto = Proyecto.Crear(request.ClienteId, request.CentroId, request.Nombre, request.FechaInicio, request.FechaFinPrevista, request.Notas);
        repositorio.Agregar(proyecto);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(proyecto.Id);
    }
}
