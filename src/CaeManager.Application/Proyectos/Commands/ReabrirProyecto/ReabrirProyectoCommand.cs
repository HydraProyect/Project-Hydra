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
/// evento terminado (decisión 2026-10-16), pero solo si su fecha de fin prevista queda en hoy o después: con la fecha
/// pasada el proceso programado lo volvería a cerrar, así que se rechaza con <c>Proyecto.EventoFinPasado</c>. La nueva
/// fecha de fin llega en <see cref="NuevaFechaFinPrevista"/> (solo eventos; el diálogo de «Reabrir» la pide cuando la de
/// fin ya pasó) y se guarda en el mismo paso. Los participantes que el cierre dio de baja no se reactivan solos. Queda
/// auditado como cualquier escritura (interceptor de auditoría, actor = la persona).
/// </summary>
public record ReabrirProyectoCommand(Guid Id, Guid Version = default, DateOnly? NuevaFechaFinPrevista = null) : ICommand;

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

        if (request.NuevaFechaFinPrevista is { } nueva)
        {
            if (!proyecto.EsEvento)
                return Result.Fallo(Error.Crear("Proyecto.NuevaFechaFinSoloEvento", "Solo un evento cambia su fecha de fin al reabrirse."));
            if (nueva < proyecto.FechaInicio)
                return Result.Fallo(Error.Crear("Proyecto.FechaFinAnteriorAlInicio", "La fecha de fin no puede ser anterior a la de inicio."));
        }

        var hoy = DiaDeNegocio.Hoy();
        if (proyecto.EsEvento && (request.NuevaFechaFinPrevista ?? proyecto.FechaFinPrevista) < hoy)
            return Result.Fallo(Error.Crear("Proyecto.EventoFinPasado",
                "La fecha de fin de este evento ya pasó y se volvería a cerrar solo. Indica una fecha de fin de hoy o posterior para reabrirlo."));

        proyecto.Reabrir(hoy, request.NuevaFechaFinPrevista);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
