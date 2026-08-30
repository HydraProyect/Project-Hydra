using CaeManager.Application.Common;
using CaeManager.Application.Plantillas.Commands.GenerarDocumentoIndividual;
using FluentValidation;

namespace CaeManager.Application.Plantillas.Commands.IniciarLoteGeneracionDocumentos;

/// <summary>
/// Crea el <see cref="Domain.Plantillas.LoteGeneracionDocumento"/> y un
/// <see cref="Domain.Plantillas.ItemGeneracionDocumento"/> por cada
/// Trabajador — sin rellenar ni un solo PDF todavía (ADR-010 § 2.6): el
/// lote existe y es consultable por Query desde este mismo momento, aunque
/// el trabajo pesado (uno por uno, vía
/// <see cref="ProcesarItemLoteGeneracion.ProcesarItemLoteGeneracionCommand"/>)
/// todavía no haya empezado. Ejecución síncrona-inmediata en este MVP: es la
/// UI (<c>ConfigurarPlantilla.razor</c>) quien recorre los items y los
/// procesa uno a uno dentro del mismo circuito Blazor — sin benchmark real
/// de coste por documento, ADR-010 § 2.6 no fija todavía cuándo pasar a una
/// cola encolada.
/// </summary>
public record IniciarLoteGeneracionDocumentosCommand(
    Guid PlantillaDocumentoVersionId,
    IReadOnlyList<Guid> TrabajadorIds,
    Guid? CentroId = null,
    IReadOnlyDictionary<Guid, string>? ValoresManuales = null)
    : ICommand<IniciarLoteGeneracionDocumentosResultadoDto>;

/// <summary><paramref name="ItemIds"/> en el mismo orden que <see cref="IniciarLoteGeneracionDocumentosCommand.TrabajadorIds"/> (tras deduplicar) — la UI los recorre uno a uno con <c>ProcesarItemLoteGeneracionCommand</c>, sin una consulta aparte para obtenerlos.</summary>
public record IniciarLoteGeneracionDocumentosResultadoDto(Guid LoteId, IReadOnlyList<Guid> ItemIds);

public class IniciarLoteGeneracionDocumentosCommandValidator : AbstractValidator<IniciarLoteGeneracionDocumentosCommand>
{
    // Mismos límites que GenerarDocumentoIndividualCommandValidator sobre
    // ValoresManuales (auditoría de seguridad del módulo, 2026-08-30): aquí
    // se serializan una sola vez en LoteGeneracionDocumento.ContextoJson,
    // compartido por todo el lote. Deliberadamente SIN límite sobre
    // TrabajadorIds — ADR-010 § 2.6 ("sin límite arquitectónico arbitrario")
    // decide explícitamente no imponer un tope aquí hasta tener un
    // benchmark real de coste por documento.
    public const int LongitudMaximaValorManual = GenerarDocumentoIndividualCommandValidator.LongitudMaximaValorManual;
    public const int MaximoValoresManuales = GenerarDocumentoIndividualCommandValidator.MaximoValoresManuales;

    public IniciarLoteGeneracionDocumentosCommandValidator()
    {
        RuleFor(c => c.PlantillaDocumentoVersionId).NotEmpty();
        RuleFor(c => c.TrabajadorIds).NotEmpty().WithMessage("Selecciona al menos un trabajador.");
        RuleFor(c => c.ValoresManuales)
            .Must(v => v is null || v.Count <= MaximoValoresManuales)
            .WithMessage($"No se pueden enviar más de {MaximoValoresManuales} valores manuales.");
        RuleForEach(c => c.ValoresManuales)
            .Must(kv => kv.Value is null || kv.Value.Length <= LongitudMaximaValorManual)
            .WithMessage($"Cada valor manual no puede superar {LongitudMaximaValorManual} caracteres.")
            .When(c => c.ValoresManuales is not null);
    }
}
