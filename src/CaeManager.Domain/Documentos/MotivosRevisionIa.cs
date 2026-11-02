namespace CaeManager.Domain.Documentos;

/// <summary>
/// Catálogo de las alertas deterministas que pueden figurar en <see cref="RevisionIaDocumento.Motivo"/>: lo que una
/// regla, sin el modelo, afirma contra el documento. Una revisión con alerta no se acepta en bloque (el servidor la
/// rechaza, ver <c>AplicarDeteccionIaDocumentoCommand.SoloSinAlertaDeterminista</c>): la tiene que abrir una persona,
/// ver el motivo y aceptarla de una en una.
///
/// <b>Por qué se decide sobre el texto de <see cref="RevisionIaDocumento.Motivo"/>.</b> La revisión no tiene una columna
/// estructurada para esto y añadirla exige migración; mientras tanto, el motivo es el único dato persistido. Es seguro
/// porque el motivo lo compone el servidor con estas constantes (el texto del documento y la respuesta del modelo no
/// entran en él) y, además, alcanza a las revisiones pendientes anteriores a esta regla. Dos condiciones lo sostienen:
/// quien escribe un motivo de alerta usa estas constantes, y las alertas van delante de los demás motivos para que el
/// recorte a <see cref="RevisionIaDocumento.LongitudMaximaMotivo"/> no se las lleve. Cambiar el texto de una constante
/// deja de reconocer las revisiones pendientes con el texto antiguo.
/// </summary>
public static class MotivosRevisionIa
{
    public const string SinContraste = "No se pudo contrastar la lectura automática con el texto del documento";
    public const string TextoDirigidoAlLector = "El documento contiene texto dirigido al sistema de lectura automática (posible manipulación)";
    public const string NoAptoORestriccion = "El documento menciona «no apto» o una restricción: lo tiene que decidir una persona";
    public const string FechaEmisionNoEscrita = "La fecha de emisión que dice haber leído la IA no aparece escrita en el documento";
    public const string FirmaNoCuadraConElContenido = "La firma digital no cuadra con el contenido: el documento fue alterado después de firmarse.";

    private const string MarcaLecturaParcial = "Lectura parcial:";

    /// <summary>Los textos cuya presencia en el motivo hace de la revisión una con alerta determinista.</summary>
    public static IReadOnlyList<string> Alertas { get; } =
        [SinContraste, TextoDirigidoAlLector, NoAptoORestriccion, FechaEmisionNoEscrita, FirmaNoCuadraConElContenido, MarcaLecturaParcial];

    /// <summary>La lectura no llegó a todas las páginas: el contraste tampoco, porque solo mira el texto leído.</summary>
    public static string LecturaParcial(int paginasSinLeer) =>
        $"{MarcaLecturaParcial} {paginasSinLeer} página(s) sin leer; un «no apto», una restricción o un dato que figure solo en ellas no se habrá detectado";

    public static bool ContieneAlerta(string motivo) =>
        Alertas.Any(alerta => motivo.Contains(alerta, StringComparison.Ordinal));
}
