using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Proyectos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Proyectos.Commands.ReabrirProyecto;

/// <summary>
/// Deshace un <see cref="CerrarProyecto.CerrarProyectoCommand"/>: el proyecto vuelve
/// a contar como abierto (sin fecha de cierre real). Existe para que un cierre con
/// fecha equivocada —que afecta a la facturación por días— tenga salida desde la
/// pantalla; mismas reglas de visibilidad y concurrencia que el cierre. También deshace el cierre automático de un
/// evento terminado (decisión 2026-10-16), pero solo si su fecha de fin prevista es hoy o posterior: con la fecha pasada
/// el proceso programado lo volvería a cerrar, así que se rechaza con <c>Proyecto.EventoFinPasado</c> y el usuario cambia
/// antes la fecha de fin. Queda auditado como cualquier escritura (interceptor de auditoría, actor = la persona).
/// </summary>
public record ReabrirProyectoCommand(Guid Id, Guid Version = default) : ICommand;

public class ReabrirProyectoCommandValidator : AbstractValidator<ReabrirProyectoCommand>
{
    public ReabrirProyectoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
    }
}

public class ReabrirProyectoCommandHandler(IProyectoRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<ReabrirProyectoCommand, Result>
{
    public async Task<Result> Handle(ReabrirProyectoCommand request, CancellationToken cancellationToken)
    {
        var proyecto = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (proyecto is null || !await ProyectoAutorizacion.VisibleAsync(proyecto.ClienteId, alcanceDatos, cancellationToken))
            return Result.Fallo(Error.Crear("Proyecto.NoEncontrado", "El proyecto no existe o no tienes acceso."));

        if (ConcurrenciaOptimista.Verificar(proyecto, request.Version, "este proyecto") is { } conflicto)
            return Result.Fallo(conflicto);

        if (proyecto.EstaAbierto)
            return Result.Fallo(Error.Crear("Proyecto.YaAbierto", "Este proyecto ya está abierto."));

        var hoy = DiaDeNegocio.Hoy();
        if (proyecto.EsEvento && proyecto.FechaFinPrevista < hoy)
            return Result.Fallo(Error.Crear("Proyecto.EventoFinPasado",
                "La fecha de fin de este evento ya pasó y se volvería a cerrar solo. Cambia antes la fecha de fin a hoy o a una posterior y reábrelo."));

        proyecto.Reabrir(hoy);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
