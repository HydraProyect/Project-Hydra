using CaeManager.Domain.Plantillas;

namespace CaeManager.Application.Common;

/// <summary>
/// Rellena el archivo original de una plantilla externa con valores ya
/// resueltos por el Command de generación — determinista, sin IA en este
/// camino (ADR-010 § 2.4, § E "Document Generation Strategy"). Dos motores
/// según <see cref="FormatoOrigenPlantilla"/>: AcroForm por nombre de campo,
/// o estampado por posición sobre un PDF visual.
/// </summary>
public interface IRellenadorPlantillaPdfService
{
    byte[] Rellenar(byte[] pdfOriginal, FormatoOrigenPlantilla formato, IReadOnlyList<ElementoRellenoPlantilla> elementos);
}

/// <summary>
/// Un <see cref="PlantillaElemento"/> ya resuelto a su valor final. Los
/// elementos de tipo <see cref="TipoElementoPlantilla.Firma"/> no se pasan
/// aquí — el filler no estampa firmas, eso sigue siendo
/// <c>IEstampadoFirmaEnCampoPdfService</c> como paso posterior (ADR-010 § 2.7).
/// </summary>
public record ElementoRellenoPlantilla(
    TipoElementoPlantilla Tipo,
    string? NombreCampoAcroForm,
    int Pagina,
    double X,
    double Y,
    double Ancho,
    double Alto,
    string? Valor,
    byte[]? ValorImagen = null);

/// <summary>
/// Auditoría de seguridad del módulo (2026-08-30), pendiente 3.2: antes, un
/// elemento cuyo <see cref="ElementoRellenoPlantilla.NombreCampoAcroForm"/>
/// no existía en el PDF (o venía vacío) se descartaba en silencio — el
/// documento se generaba igual, con ese campo en blanco. Con la validación
/// cruzada en confirmación (<c>ConfirmarPlantillaDocumentoVersionCommandHandler</c>,
/// pendiente 3.3) esto no debería ocurrir para una versión confirmada
/// después de ese cambio — esta excepción es la defensa en profundidad si
/// de todos modos ocurre (p. ej. una versión confirmada ANTES de esa
/// validación, que no se re-valida retroactivamente).
/// </summary>
public sealed class CamposAcroFormFaltantesException(IReadOnlyList<string> camposFaltantes)
    : Exception($"El PDF no tiene los campos AcroForm configurados: {string.Join(", ", camposFaltantes)}.")
{
    public IReadOnlyList<string> CamposFaltantes { get; } = camposFaltantes;
}
