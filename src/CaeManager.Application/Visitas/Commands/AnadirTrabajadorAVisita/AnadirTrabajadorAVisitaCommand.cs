using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Visitas.Commands.AnadirTrabajadorAVisita;

/// <summary>
/// Añade un Trabajador a una Visita desde el panel, sin pasar por el formulario de edición.
/// Misma autorización, Tenant y alcance que <c>EditarVisitaCommand</c>: rol de escritura
/// (<c>ICommand</c>), alcance de GESTIÓN sobre el Centro de la Visita, y el Trabajador sale de la
/// base general del Tenant —no se acota a la cartera, igual que al crear o editar—.
/// </summary>
public record AnadirTrabajadorAVisitaCommand(Guid VisitaId, Guid TrabajadorId, Guid Version = default) : ICommand;

public class AnadirTrabajadorAVisitaCommandValidator : AbstractValidator<AnadirTrabajadorAVisitaCommand>
{
    public AnadirTrabajadorAVisitaCommandValidator()
    {
        RuleFor(c => c.VisitaId).NotEmpty();
        RuleFor(c => c.TrabajadorId).NotEmpty();
    }
}

public class AnadirTrabajadorAVisitaCommandHandler(
    IVisitaRepository repositorio, IVisitaTrabajadorRepository visitaTrabajadorRepositorio,
    ITrabajadoresQueryContext trabajadoresContext, IEvaluadorExpedienteVisitaService evaluadorExpediente,
    IUnitOfWork unitOfWork, ILogger<AnadirTrabajadorAVisitaCommandHandler> logger, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<AnadirTrabajadorAVisitaCommand, Result>
{
    public async Task<Result> Handle(AnadirTrabajadorAVisitaCommand request, CancellationToken cancellationToken)
    {
        // Mismo orden que al editar: alcance de gestión antes que nada, para no revelar
        // qué hay fuera ni su versión; después cancelada y concurrencia.
        var visita = await repositorio.ObtenerPorIdAsync(request.VisitaId, cancellationToken);
        if (visita is null || !await AutorizacionCancelacionVisita.PuedeGestionarAsync(alcanceDatos, visita, cancellationToken))
            return Result.Fallo(AutorizacionCancelacionVisita.NoEncontrada);

        // FS-11: una Visita cancelada no se modifica; primero se reactiva.
        if (visita.EstaCancelada)
            return Result.Fallo(Error.Crear("Visita.Cancelada", "Esta visita está cancelada. Reactívala antes de modificarla."));

        if (ConcurrenciaOptimista.Verificar(visita, request.Version, "esta visita") is { } conflicto)
            return Result.Fallo(conflicto);

        // Añadir a quien ya entra no es un error: un doble clic o dos personas a la vez
        // terminan en el mismo estado.
        var actuales = await visitaTrabajadorRepositorio.ObtenerPorVisitaAsync(visita.Id, cancellationToken);
        if (actuales.Any(vt => vt.TrabajadorId == request.TrabajadorId))
            return Result.Exito();

        // Verificación de Ids ajenos — ver P0-1 de Project-Hydra-Negocio/MATURITY_REVIEW.md.
        if (!await trabajadoresContext.Trabajadores.AnyAsync(t => t.Id == request.TrabajadorId, cancellationToken))
            return Result.Fallo(Error.Crear("Visita.TrabajadorNoEncontrado", "No encontramos este trabajador."));

        visitaTrabajadorRepositorio.Agregar(new VisitaTrabajador(visita.Id, request.TrabajadorId));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Cambiar quién entra puede dejar el expediente completo.
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
