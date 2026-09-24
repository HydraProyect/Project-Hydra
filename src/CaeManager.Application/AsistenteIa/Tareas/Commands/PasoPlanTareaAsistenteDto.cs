using CaeManager.Application.AsistenteIa.Ordenes;
using CaeManager.Domain.AsistenteIa;
using FluentValidation;

namespace CaeManager.Application.AsistenteIa.Tareas.Commands;

/// <summary>
/// Un paso del plan tal como lo entrega quien interpreta la orden. Solo se fija
/// el identificador estable de la orden del catálogo y sus datos como objeto
/// JSON: la forma del puerto de interpretación puede cambiar sin tocar esto.
/// </summary>
public record PasoPlanTareaAsistenteDto(
    string OrdenAsistenteId,
    string DatosJson,
    string? Resumen,
    IReadOnlyList<string> CamposPendientes,
    IReadOnlyList<AvisoPasoTareaAsistente> Avisos)
{
    internal DefinicionPasoTareaAsistente ADefinicion() =>
        new(OrdenAsistenteId, DatosJson, Resumen, CamposPendientes, Avisos);
}

/// <summary>
/// La orden tiene que existir en <see cref="CatalogoOrdenesAsistente"/> y no ser
/// la abstención: un paso sin orden no es un paso.
/// </summary>
public class PasoPlanTareaAsistenteDtoValidator : AbstractValidator<PasoPlanTareaAsistenteDto>
{
    public PasoPlanTareaAsistenteDtoValidator()
    {
        RuleFor(p => p.OrdenAsistenteId)
            .Must(EsOrdenDelCatalogo)
            .WithMessage(p => $"La orden «{p.OrdenAsistenteId}» no está en el catálogo del asistente.");
        RuleFor(p => p.DatosJson).NotEmpty().MaximumLength(PasoTareaAsistente.LongitudMaximaDatosJson);
        RuleFor(p => p.Resumen).MaximumLength(PasoTareaAsistente.LongitudMaximaResumen);
        RuleFor(p => p.CamposPendientes).NotNull();
        RuleFor(p => p.Avisos).NotNull();
    }

    internal static bool EsOrdenDelCatalogo(string? ordenAsistenteId) =>
        ordenAsistenteId is not null
        && ordenAsistenteId != CatalogoOrdenesAsistente.Abstencion
        && CatalogoOrdenesAsistente.PorId(ordenAsistenteId) is not null;
}
