using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using CaeManager.Application.Visitas.Antelacion;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Visitas.Commands.ReactivarVisita;

/// <summary>
/// Deshace la cancelación de una Visita (FS-11) y la devuelve al estado previo:
/// activa o finalizada según sus fechas, que cancelar no tocó. El motivo es
/// opcional. La auditoría registra el Actor real, el Usuario simulado si hay
/// impersonación, la fecha y el motivo (<see cref="Visita.MotivoReactivacion"/>).
/// <para>
/// Quién puede reactivar es exactamente quien puede cancelar (decisión de la
/// coordinadora, 2026-09-26): la misma <see cref="AutorizacionCancelacionVisita"/>.
/// </para>
/// <para>
/// Concurrencia (contrato de la ficha 09, mismo patrón que
/// <c>RestaurarAnotacionAcreditacionCommand</c>): la versión que el usuario vio es
/// obligatoria y se compara. Si la Visita cambió desde entonces —otra persona la
/// reactivó y la volvió a cancelar con otro motivo, por ejemplo—, se rechaza con
/// <see cref="ConcurrenciaOptimista.CodigoConflicto"/> en vez de pisar ese cambio.
/// Idempotente: reactivar una Visita que ya no está cancelada es éxito sin escribir,
/// sin auditar y sin reevaluar el expediente (el estado pedido ya es el actual).
/// </para>
/// </summary>
public record ReactivarVisitaCommand(Guid Id, Guid VersionEsperada, string? Motivo = null) : ICommand;

public class ReactivarVisitaCommandValidator : AbstractValidator<ReactivarVisitaCommand>
{
    public ReactivarVisitaCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        // Guid.Empty en ConcurrenciaOptimista significa «sin comprobación»: aquí no vale.
        RuleFor(c => c.VersionEsperada).NotEmpty();
        RuleFor(c => c.Motivo).MaximumLength(Visita.LongitudMaximaMotivo);
    }
}

public class ReactivarVisitaCommandHandler(
    IVisitaRepository repositorio, IUnitOfWork unitOfWork, IAlcanceDatosService alcanceDatos,
    IEvaluadorExpedienteVisitaService evaluadorExpediente, ILogger<ReactivarVisitaCommandHandler> logger)
    : IRequestHandler<ReactivarVisitaCommand, Result>
{
    public async Task<Result> Handle(ReactivarVisitaCommand request, CancellationToken cancellationToken)
    {
        // Releída de la base de datos: se compara contra el estado actual, no contra una
        // copia que este circuito rastreara antes.
        var visita = await repositorio.ObtenerPorIdActualizadoAsync(request.Id, cancellationToken);
        if (visita is null || !await AutorizacionCancelacionVisita.PuedeGestionarAsync(alcanceDatos, visita, cancellationToken))
            return Result.Fallo(AutorizacionCancelacionVisita.NoEncontrada);

        // Idempotente, y antes de la versión: si ya está reactivada (p. ej. un segundo «Deshacer»
        // o el de otra pestaña), el estado pedido ya es el actual.
        if (!visita.EstaCancelada)
            return Result.Exito();

        if (ConcurrenciaOptimista.Verificar(visita, request.VersionEsperada, "esta visita") is not null)
            return Result.Fallo(Error.Crear(
                ConcurrenciaOptimista.CodigoConflicto,
                "Esta visita cambió desde que la viste: otra persona la modificó. Hemos recargado la lista; revisa su estado y reactívala de nuevo si sigue cancelada."));

        visita.Reactivar(DateTime.UtcNow, request.Motivo);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Mientras estaba cancelada el evaluador la saltaba: si su documentación
        // se completó entretanto, el expediente se sella ahora y no al siguiente
        // cambio de un Documento. Mismo criterio que EditarVisitaCommand.
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
