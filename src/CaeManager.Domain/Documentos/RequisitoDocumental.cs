namespace CaeManager.Domain.Documentos;

/// <summary>
/// <b>¿Pedimos este documento?</b> — uno de los dos ejes que sustituyen al
/// antiguo <c>TipoDocumento.EsObligatorio</c>.
///
/// <para>
/// Aquel booleano respondía a la vez a dos preguntas distintas —«¿lo pedimos?»
/// y «¿con qué autoridad?»— y colapsarlas obligaba a mentir en una de las dos:
/// o se marcaba obligatorio el seguro de responsabilidad civil, afirmando una
/// ley que no existe, o se desmarcaba y se dejaba de pedir un documento que
/// piden todos los centros. La autoridad vive ahora en
/// <see cref="NaturalezaJuridica"/>.
/// </para>
///
/// <para>
/// Valores explícitos y congelados: este enum sale por la API v1 y se compara
/// en consultas — ver <c>OrdinalesDeEnumsPublicadosTests</c>.
/// </para>
/// </summary>
public enum RequisitoDocumental
{
    /// <summary>No forma parte de lo que se pide por defecto.</summary>
    No = 0,

    /// <summary>
    /// Se pide siempre. Es lo único que cuenta para el cumplimiento — sea
    /// porque lo exige una norma o porque lo exige la práctica del sector:
    /// los dos acaban siendo documentos que todos los centros piden igual.
    /// </summary>
    Si = 1,

    /// <summary>
    /// Se activa por actividad, sector o situación (REA → obra de
    /// construcción, A1 → desplazamiento, formación de convenio…).
    ///
    /// <para>
    /// <b>No cuenta para el cumplimiento</b>, y no es una omisión: la
    /// maquinaria que evalúa la condición todavía no existe. Contar una
    /// condición que no se puede evaluar no es prudencia, es un falso
    /// positivo sistemático — pondría en rojo a toda empresa industrial que
    /// no pisa una obra en su vida.
    /// </para>
    /// </summary>
    Condicional = 2,
}
