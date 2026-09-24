using CaeManager.Application.Common;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.AsistenteIa.Tareas.Commands;

/// <summary>
/// Reanuda un paso en borrador cuando llega el dato que faltaba (o cambia uno).
/// El paso pasa a listo si ya no le falta nada ni tiene avisos bloqueantes.
/// </summary>
public record ActualizarPasoTareaAsistenteCommand(
    Guid TareaId,
    Guid PasoId,
    string DatosJson,
    string? Resumen,
    IReadOnlyList<string> CamposPendientes,
    IReadOnlyList<AvisoPasoTareaAsistente> Avisos) : ICommand;

public class ActualizarPasoTareaAsistenteCommandValidator : AbstractValidator<ActualizarPasoTareaAsistenteCommand>
{
    public ActualizarPasoTareaAsistenteCommandValidator()
    {
        RuleFor(c => c.TareaId).NotEmpty();
        RuleFor(c => c.PasoId).NotEmpty();
        RuleFor(c => c.DatosJson).NotEmpty().MaximumLength(PasoTareaAsistente.LongitudMaximaDatosJson);
        RuleFor(c => c.Resumen).MaximumLength(PasoTareaAsistente.LongitudMaximaResumen);
        RuleFor(c => c.CamposPendientes).NotNull();
        RuleFor(c => c.Avisos).NotNull();
    }
}

public class ActualizarPasoTareaAsistenteCommandHandler(ModificacionTareaAsistente modificacion)
    : IRequestHandler<ActualizarPasoTareaAsistenteCommand, Result>
{
    public Task<Result> Handle(ActualizarPasoTareaAsistenteCommand request, CancellationToken cancellationToken) =>
        modificacion.AplicarAsync(request.TareaId, versionEsperada: null, (tarea, _) =>
            tarea.ActualizarPasoEnBorrador(
                request.PasoId, request.DatosJson, request.Resumen, request.CamposPendientes, request.Avisos, DateTime.UtcNow),
            cancellationToken);
}
