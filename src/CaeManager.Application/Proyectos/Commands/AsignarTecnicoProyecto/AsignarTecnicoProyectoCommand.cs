using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Proyectos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Proyectos.Commands.AsignarTecnicoProyecto;

public record AsignarTecnicoProyectoCommand(Guid ProyectoId, Guid TrabajadorId, DateOnly FechaAlta)
    : ICommand<Guid>;

public class AsignarTecnicoProyectoCommandValidator : AbstractValidator<AsignarTecnicoProyectoCommand>
{
    public AsignarTecnicoProyectoCommandValidator()
    {
        RuleFor(c => c.ProyectoId).NotEmpty();
        RuleFor(c => c.TrabajadorId).NotEmpty().WithMessage("Selecciona un técnico.");
    }
}

public class AsignarTecnicoProyectoCommandHandler(
    IProyectoTecnicoRepository repositorio, IProyectoRepository proyectoRepositorio,
    ITrabajadoresQueryContext dbContext, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<AsignarTecnicoProyectoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AsignarTecnicoProyectoCommand request, CancellationToken cancellationToken)
    {
        var proyecto = await proyectoRepositorio.ObtenerPorIdAsync(request.ProyectoId, cancellationToken);
        if (proyecto is null || !await ProyectoAutorizacion.VisibleAsync(proyecto.ClienteId, alcanceDatos, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Proyecto.NoEncontrado", "El proyecto no existe o no tienes acceso."));

        // Auditoría Módulo 5, hallazgo crítico 6/9 (extendido a Proyecto): la
        // existencia del trabajador no basta — tiene que estar bajo la
        // autoridad del actor, o un técnico ajeno a la cartera queda
        // introducido en el alcance visible en cuanto se le asigna.
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var trabajadorVisible = trabajadorIdsVisibles is null || trabajadorIdsVisibles.Contains(request.TrabajadorId);
        var trabajadorExiste = trabajadorVisible
            && await dbContext.Trabajadores.AnyAsync(t => t.Id == request.TrabajadorId, cancellationToken);
        if (!trabajadorExiste)
            return Result.Fallo<Guid>(Error.Crear("Proyecto.TrabajadorNoEncontrado", "No encontramos este trabajador."));

        if (await repositorio.ExisteActivoAsync(request.ProyectoId, request.TrabajadorId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Proyecto.TecnicoYaAsignado", "Este técnico ya está asignado al proyecto."));

        var proyectoTecnico = new ProyectoTecnico(request.ProyectoId, request.TrabajadorId, request.FechaAlta);
        repositorio.Agregar(proyectoTecnico);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(proyectoTecnico.Id);
    }
}
