using CaeManager.Application.Common;
using CaeManager.Domain.Plantillas;
using FluentValidation;

namespace CaeManager.Application.Plantillas.Commands.GuardarElementosPlantilla;

/// <summary>
/// Reemplaza el conjunto completo de <see cref="PlantillaElemento"/> de una
/// versión no confirmada — misma llamada tanto tras la detección inicial
/// automática (PR4) como tras cada guardado manual del editor visual
/// (mover/redimensionar/añadir/eliminar cajas). Nunca "añade uno suelto"
/// (ver <see cref="PlantillaDocumentoVersion.EstablecerElementos"/>): los Id
/// de <see cref="PlantillaElemento"/> no sobreviven entre guardados — nada
/// más los referencia todavía en este MVP (la generación de documentos, que
/// sí lo haría, es un PR posterior), así que no hace falta reconciliarlos.
/// </summary>
public record GuardarElementosPlantillaCommand(
    Guid PlantillaDocumentoVersionId,
    IReadOnlyList<ElementoPlantillaEntradaDto> Elementos)
    : ICommand;

public record ElementoPlantillaEntradaDto(
    TipoElementoPlantilla Tipo,
    int Pagina,
    double X,
    double Y,
    double Ancho,
    double Alto,
    string EtiquetaVisible,
    FuenteDatoPlantilla? FuenteDato,
    string? ValorConstante,
    string? Formato,
    bool Obligatorio,
    RolFirmantePlantilla? RolFirmante,
    string? NombreCampoAcroForm);

public class GuardarElementosPlantillaCommandValidator : AbstractValidator<GuardarElementosPlantillaCommand>
{
    public GuardarElementosPlantillaCommandValidator()
    {
        RuleFor(c => c.PlantillaDocumentoVersionId).NotEmpty();
    }
}
