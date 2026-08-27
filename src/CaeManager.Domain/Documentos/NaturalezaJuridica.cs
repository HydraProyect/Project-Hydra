namespace CaeManager.Domain.Documentos;

/// <summary>
/// <b>¿Con qué autoridad pedimos este documento?</b> — el segundo eje que
/// sustituye al antiguo <c>TipoDocumento.EsObligatorio</c>, y el que permite
/// responder a «¿por qué me pides esto?» sin mentir.
///
/// <para>
/// Es independiente de <see cref="RequisitoDocumental"/>: un documento puede
/// ser requerido por práctica consolidada sin ser obligación legal, y hay que
/// pedirlo igual — pero <b>sin llamarlo ley</b>. Exagerar aquí es
/// exactamente el fallo que esta taxonomía existe para impedir.
/// </para>
///
/// <para>
/// Tres afirmaciones que el producto no puede hacer nunca, y que ninguna
/// combinación de estos valores debe permitir: que la vigilancia de la salud
/// es obligatoria para el trabajador (art. 22.1 LPRL: requiere su
/// consentimiento), que el seguro de responsabilidad civil lo exige la ley
/// (no existe obligación general en España), y que los antecedentes penales
/// son documentación CAE.
/// </para>
///
/// <para>
/// Valores explícitos y congelados: sale por la API v1 — ver
/// <c>OrdinalesDeEnumsPublicadosTests</c>.
/// </para>
/// </summary>
public enum NaturalezaJuridica
{
    /// <summary>
    /// Una norma lo exige, sin condiciones. En el catálogo actual esto es
    /// prácticamente solo la evaluación de riesgos y la planificación de la
    /// actividad preventiva: el art. 10 del RD 171/2004 es lo único que se
    /// exige por escrito.
    /// </summary>
    ObligacionLegal = 0,

    /// <summary>
    /// Una norma lo exige <b>en un supuesto concreto</b>: REA en obra de
    /// construcción, recursos preventivos según la actividad, documentación
    /// traducida en desplazamiento (Ley 45/1999 art. 6.5 — traducida, no
    /// jurada).
    /// </summary>
    ObligacionCondicionada = 1,

    /// <summary>
    /// Ninguna norma lo exige, pero **lo piden todos los centros**: el
    /// registro firmado de entrega de EPI, el certificado de estar al
    /// corriente con la TGSS. Se pide igual; se justifica como práctica.
    /// </summary>
    PracticaSector = 2,

    /// <summary>
    /// Lo exige un cliente o un centro concreto, no el sector ni la norma.
    /// Es también la naturaleza de los tipos que un tenant crea por su
    /// cuenta — y no por comodidad: un tipo que se inventó un cliente es,
    /// por definición, requisito de ese cliente. Lo que sería falso es
    /// suponerle una obligación legal o una práctica del sector que nadie ha
    /// verificado.
    /// </summary>
    RequisitoCliente = 3,

    /// <summary>Lo propone TALVEG por experiencia operativa. Nunca se rotula
    /// como obligatorio.</summary>
    Recomendacion = 4,
}
