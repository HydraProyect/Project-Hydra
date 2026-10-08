using CaeManager.Application.Common;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Visitas.Commands.QuitarTrabajadorDeVisita;

/// <summary>
/// Quita un Trabajador de una Visita desde el panel. Misma autorización, Tenant y alcance que
/// <c>EditarVisitaCommand</c>, y su misma regla: una Visita no se queda sin Trabajadores.
/// </summary>
public record QuitarTrabajadorDeVisitaCommand(Guid VisitaId, Guid TrabajadorId, Guid Version = default) : ICommand;

public class QuitarTrabajadorDeVisitaCommandValidator : AbstractValidator<QuitarTrabajadorDeVisitaCommand>
{
    public QuitarTrabajadorDeVisitaCommandValidator()
    {
        RuleFor(c => c.VisitaId).NotEmpty();
        RuleFor(c => c.TrabajadorId).NotEmpty();
    }
}

public class QuitarTrabajadorDeVisitaCommandHandler(
    IVisitaRepository repositorio, IVisitaTrabajadorRepository visitaTrabajadorRepositorio,
    IEvaluadorExpedienteVisitaService evaluadorExpediente,
    IUnitOfWork unitOfWork, ILogger<QuitarTrabajadorDeVisitaCommandHandler> logger, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<QuitarTrabajadorDeVisitaCommand, Result>
{
    public const string CodigoUltimoTrabajador = "Visita.UltimoTrabajador";

    public async Task<Result> Handle(QuitarTrabajadorDeVisitaCommand request, CancellationToken cancellationToken)
    {
        var visita = await repositorio.ObtenerPorIdAsync(request.VisitaId, cancellationToken);
        if (visita is null || !await AutorizacionCancelacionVisita.PuedeGestionarAsync(alcanceDatos, visita, cancellationToken))
            return Result.Fallo(AutorizacionCancelacionVisita.NoEncontrada);

        // FS-11: una Visita cancelada no se modifica; primero se reactiva.
        if (visita.EstaCancelada)
            return Result.Fallo(Error.Crear("Visita.Cancelada", "Esta visita está cancelada. Reactívala antes de modificarla."));

        if (ConcurrenciaOptimista.Verificar(visita, request.Version, "esta visita") is { } conflicto)
            return Result.Fallo(conflicto);

        var actuales = await visitaTrabajadorRepositorio.ObtenerPorVisitaAsync(visita.Id, cancellationToken);
        var aQuitar = actuales.FirstOrDefault(vt => vt.TrabajadorId == request.TrabajadorId);

        // Quitar a quien ya no entra no es un error: mismo estado final.
        if (aQuitar is null)
            return Result.Exito();

        // La misma regla que el validador de EditarVisitaCommand, aplicada sobre lo que hay
        // guardado y no sobre lo que la pantalla creía tener.
        if (actuales.Count == 1)
            return Result.Fallo(Error.Crear(CodigoUltimoTrabajador, "La visita debe incluir al menos un trabajador."));

        visitaTrabajadorRepositorio.Eliminar(aQuitar);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Con un Trabajador menos puede dejar de faltar documentación.
        try
        {
            await evaluadorExpediente.EvaluarAsync(visita.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo evaluar el expediente documental de la visita {VisitaId}.", visita.Id);
        }

        return Result.Exito();
    }
}
