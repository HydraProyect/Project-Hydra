using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Proyectos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Proyectos.Commands.ActualizarProyecto;

public record ActualizarProyectoCommand(
    Guid Id, string Nombre, DateOnly? FechaFinPrevista, string? Notas, Guid Version = default)
    : ICommand;

public class ActualizarProyectoCommandValidator : AbstractValidator<ActualizarProyectoCommand>
{
    public ActualizarProyectoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Nombre).NotEmpty().MaximumLength(Proyecto.LongitudMaximaNombre);
        RuleFor(c => c.Notas).MaximumLength(Proyecto.LongitudMaximaNotas);
    }
}

public class ActualizarProyectoCommandHandler(IProyectoRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<ActualizarProyectoCommand, Result>
{
    public async Task<Result> Handle(ActualizarProyectoCommand request, CancellationToken cancellationToken)
    {
        var proyecto = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (proyecto is null || !await ProyectoAutorizacion.VisibleAsync(proyecto.ClienteId, alcanceDatos, cancellationToken))
            return Result.Fallo(Error.Crear("Proyecto.NoEncontrado", "El proyecto no existe o no tienes acceso."));

        if (ConcurrenciaOptimista.Verificar(proyecto, request.Version, "este proyecto") is { } conflicto)
            return Result.Fallo(conflicto);

        if (!string.Equals(proyecto.Nombre, request.Nombre.Trim(), StringComparison.Ordinal)
            && await repositorio.ExisteNombreParaClienteAsync(proyecto.ClienteId, request.Nombre, proyecto.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Proyecto.NombreDuplicado", "Ya existe un proyecto con este nombre para el cliente seleccionado."));

        proyecto.Actualizar(request.Nombre, request.FechaFinPrevista, request.Notas);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
