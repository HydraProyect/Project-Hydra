using CaeManager.Application.Common;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.AsistenteIa.Tareas.Commands;

/// <summary>
/// Añade un turno a la conversación: lo que la persona escribe, o lo que el
/// asistente le respondió. Mismo reparto de textos que al crear la tarea.
/// </summary>
public record AgregarTurnoTareaAsistenteCommand(
    Guid TareaId,
    AutorTurnoTareaAsistente Autor,
    string TextoOriginal,
    string? TextoEnmascarado = null) : ICommand;

public class AgregarTurnoTareaAsistenteCommandValidator : AbstractValidator<AgregarTurnoTareaAsistenteCommand>
{
    public AgregarTurnoTareaAsistenteCommandValidator()
    {
        RuleFor(c => c.TareaId).NotEmpty();
        RuleFor(c => c.Autor).IsInEnum();
        RuleFor(c => c.TextoOriginal).NotEmpty().MaximumLength(TurnoTareaAsistente.LongitudMaximaTexto);
        RuleFor(c => c.TextoEnmascarado).MaximumLength(TurnoTareaAsistente.LongitudMaximaTexto);
    }
}

public class AgregarTurnoTareaAsistenteCommandHandler(ModificacionTareaAsistente modificacion)
    : IRequestHandler<AgregarTurnoTareaAsistenteCommand, Result>
{
    public Task<Result> Handle(AgregarTurnoTareaAsistenteCommand request, CancellationToken cancellationToken) =>
        modificacion.AplicarAsync(request.TareaId, versionEsperada: null, (tarea, _) =>
        {
            var ahora = DateTime.UtcNow;
            if (request.Autor == AutorTurnoTareaAsistente.Persona)
                tarea.AgregarTurnoDePersona(request.TextoOriginal, request.TextoEnmascarado, ahora);
            else
                tarea.AgregarTurnoDeAsistente(request.TextoOriginal, request.TextoEnmascarado, ahora);
        }, cancellationToken);
}
