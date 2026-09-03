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
    ResultadoRellenoPlantilla Rellenar(byte[] pdfOriginal, FormatoOrigenPlantilla formato, IReadOnlyList<ElementoRellenoPlantilla> elementos);
}

/// <summary>
/// Un <see cref="PlantillaElemento"/> ya resuelto a su valor final. Los
/// elementos de tipo <see cref="TipoElementoPlantilla.Firma"/> no se pasan
/// aquí — el filler no estampa firmas, eso sigue siendo
/// <c>IEstampadoFirmaEnCampoPdfService</c> como paso posterior (ADR-010 § 2.7).
///
/// <paramref name="ElementoId"/> es el <c>PlantillaElemento.Id</c> de origen —
/// el filler no conoce <c>EtiquetaVisible</c> (vive en Application/Domain), así
/// que cualquier aviso que emita (<see cref="AvisoValorNoReconocido"/>) nombra
/// el elemento por este Id opaco; quien llama lo traduce a la etiqueta visible
/// cruzando contra <c>PlantillaDocumentoVersion.Elementos</c>. Con valor por
/// defecto para no romper los tests existentes que no lo necesitan.
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
    byte[]? ValorImagen = null,
    Guid ElementoId = default);

/// <summary>
/// Resultado de <see cref="IRellenadorPlantillaPdfService.Rellenar"/> (DEC-32,
/// REC-115): el PDF se genera siempre — <see cref="ValoresNoReconocidos"/> es
/// la lista de avisos, vacía cuando no hay nada que avisar, nunca una señal de
/// fallo. El documento se guarda igual aunque la lista no esté vacía.
/// </summary>
public record ResultadoRellenoPlantilla(byte[] Pdf, IReadOnlyList<AvisoValorNoReconocido> ValoresNoReconocidos);

/// <summary>
/// Un valor de entrada que no corresponde a ninguna opción reconocida del
/// campo (DEC-32, REC-115): en un radio, ninguna opción real del grupo (por
/// <c>/Opt</c> o por nombre de estado); en un checkbox, ni el estado «on» que
/// el propio widget declara ni el conjunto documentado de afirmativos/negativos.
/// Un valor vacío/solo-espacios NO genera este aviso — "no contestado" es un
/// caso distinto de "contestado con algo que este formulario no reconoce", y
/// el primero ya lo cubre DEC-5 (obligatorio vacío) en Application cuando
/// corresponde. <paramref name="OpcionesDisponibles"/> son las opciones reales
/// del campo (radio) o el contrato documentado de valores soportados
/// (checkbox) — información para que el aviso sea accionable, no solo una queja.
/// </summary>
public record AvisoValorNoReconocido(Guid ElementoId, string? ValorRecibido, IReadOnlyList<string> OpcionesDisponibles);

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
