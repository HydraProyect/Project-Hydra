using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using FluentValidation;

namespace CaeManager.Application.Plantillas.Commands.CrearPlantillaDocumento;

/// <summary>
/// Alta de una plantilla <see cref="OrigenPlantilla.Externa"/> — el PDF que
/// entrega un centro. Crea el encabezado (<see cref="PlantillaDocumento"/>)
/// y su primera versión en <see cref="EstadoConfiguracionPlantilla.Borrador"/>
/// de una sola vez: sin campos configurados todavía, el editor visual (Query
/// de detección de PR4 + confirmación) es un paso aparte (ADR-010 § 2.3).
/// Plantillas <see cref="OrigenPlantilla.TalvegPropio"/> no se dan de alta
/// por aquí — su catálogo se siembra por código (§ 2.9).
/// </summary>
public record CrearPlantillaDocumentoCommand(
    string Nombre,
    AmbitoAplicacion AmbitoAplicacion,
    FormatoOrigenPlantilla FormatoOrigen,
    Guid TipoDocumentoId,
    byte[] Contenido,
    string NombreArchivo,
    string? Descripcion = null,
    Guid? CentroId = null,
    Guid? ClienteId = null)
    : ICommand<CrearPlantillaDocumentoResultadoDto>;

public record CrearPlantillaDocumentoResultadoDto(Guid PlantillaDocumentoId, Guid PlantillaDocumentoVersionId);

public class CrearPlantillaDocumentoCommandValidator : AbstractValidator<CrearPlantillaDocumentoCommand>
{
    public CrearPlantillaDocumentoCommandValidator()
    {
        RuleFor(c => c.Nombre).NotEmpty().MaximumLength(PlantillaDocumento.LongitudMaximaNombre);
        RuleFor(c => c.Descripcion).MaximumLength(PlantillaDocumento.LongitudMaximaDescripcion);
        RuleFor(c => c.TipoDocumentoId).NotEmpty().WithMessage("Selecciona bajo qué tipo de documento cuentan los documentos generados.");
        RuleFor(c => c.Contenido).NotEmpty().WithMessage("El PDF de la plantilla es obligatorio.");
        RuleFor(c => c.NombreArchivo).NotEmpty();
        RuleFor(c => c.FormatoOrigen)
            .NotEqual(FormatoOrigenPlantilla.HtmlCss)
            .WithMessage("HtmlCss es exclusivo de plantillas TalvegPropio — esta alta solo crea plantillas externas.");
    }
}
