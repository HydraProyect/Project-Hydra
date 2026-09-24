using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.AsistenteIa.Tareas.Commands;

/// <summary>
/// Descarta la tarea entera o, con <paramref name="PasoId"/>, un solo paso del
/// plan antes de confirmarlo. Descartar no deshace lo ya ejecutado.
/// </summary>
public record DescartarTareaAsistenteCommand(Guid TareaId, Guid? PasoId = null) : ICommand;

public class DescartarTareaAsistenteCommandValidator : AbstractValidator<DescartarTareaAsistenteCommand>
{
    public DescartarTareaAsistenteCommandValidator()
    {
        RuleFor(c => c.TareaId).NotEmpty();
        RuleFor(c => c.PasoId).NotEqual(Guid.Empty);
    }
}

public class DescartarTareaAsistenteCommandHandler(ModificacionTareaAsistente modificacion)
    : IRequestHandler<DescartarTareaAsistenteCommand, Result>
{
    public Task<Result> Handle(DescartarTareaAsistenteCommand request, CancellationToken cancellationToken) =>
        modificacion.AplicarAsync(request.TareaId, versionEsperada: null, (tarea, _) =>
        {
            if (request.PasoId is { } pasoId)
                tarea.DescartarPaso(pasoId, DateTime.UtcNow);
            else
                tarea.Descartar(DateTime.UtcNow);
        }, cancellationToken);
}
