namespace CaeManager.Domain.Documentos;

/// <summary>
/// Catálogo de las alertas deterministas que pueden figurar en <see cref="RevisionIaDocumento.Motivo"/>: lo que una
/// regla, sin el modelo, afirma contra el documento. Una revisión con alerta no se acepta en bloque (el servidor la
/// rechaza, ver <c>AplicarDeteccionIaDocumentoCommand.SoloSinAlertaDeterminista</c>): la tiene que abrir una persona,
/// ver el motivo y aceptarla de una en una.
///
/// <b>Por qué se decide sobre el texto de <see cref="RevisionIaDocumento.Motivo"/>.</b> La revisión no tiene una columna
/// estructurada para esto y añadirla exige migración; mientras tanto, el motivo es el único dato persistido. Es seguro
/// porque el motivo lo compone el servidor con este catálogo: la respuesta del modelo no entra en él y, del documento,
/// solo el CIF que extrae el parser determinista, acotado por su patrón. Un texto del documento no puede quitar una alerta.
/// Dos condiciones lo sostienen: quien escribe un motivo de alerta usa este catálogo, y las alertas van delante de los
/// demás motivos para que el recorte a <see cref="RevisionIaDocumento.LongitudMaximaMotivo"/> no se las lleve.
///
/// <b>Revisiones pendientes anteriores a esta regla.</b> Se reconocen las que ya llevan estos textos, que no cambiaron al
/// pasar al catálogo. No las de lectura parcial: se crearon sin esa marca y no hay backfill, así que siguen siendo
/// aceptables en bloque hasta que se reprocesen. Cambiar el texto de una alerta deja de reconocer las pendientes con el
/// texto antiguo; por eso los literales persistidos están fijados a mano en <c>MotivosRevisionIaTests</c>.
/// </summary>
public static class MotivosRevisionIa
{
    public const string SinContraste = "No se pudo contrastar la lectura automática con el texto del documento";
    public const string TextoDirigidoAlLector = "El documento contiene texto dirigido al sistema de lectura automática (posible manipulación)";
    public const string NoAptoORestriccion = "El documento menciona «no apto» o una restricción: lo tiene que decidir una persona";
    public const string FechaEmisionNoEscrita = "La fecha de emisión que dice haber leído la IA no aparece escrita en el documento";
    public const string FirmaNoCuadraConElContenido = "La firma digital no cuadra con el contenido: el documento fue alterado después de firmarse.";
    public const string CertificadoEnNegativo = "El certificado está emitido EN NEGATIVO — declara que la empresa no está al corriente.";

    private const string MarcaLecturaParcial = "Lectura parcial:";
    private const string MarcaCifNoCoincide = ") no coincide con el del propietario (";

    /// <summary>Los textos cuya presencia en el motivo hace de la revisión una con alerta determinista.</summary>
    public static IReadOnlyList<string> Alertas { get; } =
    [
        SinContraste,
        TextoDirigidoAlLector,
        NoAptoORestriccion,
        FechaEmisionNoEscrita,
        FirmaNoCuadraConElContenido,
        CertificadoEnNegativo,
        MarcaCifNoCoincide,
        MarcaLecturaParcial,
    ];

    /// <summary>El documento oficial identifica a otra empresa: la marca es la parte fija del texto, sin los dos CIF.</summary>
    public static string CifNoCoincideConElPropietario(string cifDocumento, string cifPropietario) =>
        $"El CIF del documento ({cifDocumento}{MarcaCifNoCoincide}{cifPropietario}).";

    /// <summary>La lectura no llegó a todas las páginas: el contraste tampoco, porque solo mira el texto leído.</summary>
    public static string LecturaParcial(int paginasSinLeer) =>
        $"{MarcaLecturaParcial} {paginasSinLeer} página(s) sin leer; un «no apto», una restricción o un dato que figure solo en ellas no se habrá detectado";

    public static bool ContieneAlerta(string motivo) =>
        Alertas.Any(alerta => motivo.Contains(alerta, StringComparison.Ordinal));
}
