using CaeManager.Application.Common;
using FluentValidation;

namespace CaeManager.Application.Plantillas.Commands.ProcesarItemLoteGeneracion;

/// <summary>
/// Procesa un único <see cref="Domain.Plantillas.ItemGeneracionDocumento"/> —
/// reutiliza <see cref="GenerarDocumentoIndividual.GenerarDocumentoIndividualCommand"/>
/// entero (vía <c>IMediator</c>, mismo criterio que otros Commands de este
/// repo que componen otro Command) en vez de duplicar la resolución de
/// datos y el relleno del PDF. La UI llama a este Command una vez por item,
/// en un bucle dentro del mismo circuito Blazor (ADR-010 § 2.6,
/// "progreso en vivo dentro del circuito", mismo patrón que
/// <c>SubidaMasiva.razor.cs</c>) — nunca falla el lote entero por un item
/// que no se puede resolver, solo ese item.
/// </summary>
public record ProcesarItemLoteGeneracionCommand(Guid ItemGeneracionDocumentoId) : ICommand;

public class ProcesarItemLoteGeneracionCommandValidator : AbstractValidator<ProcesarItemLoteGeneracionCommand>
{
    public ProcesarItemLoteGeneracionCommandValidator()
    {
        RuleFor(c => c.ItemGeneracionDocumentoId).NotEmpty();
    }
}
