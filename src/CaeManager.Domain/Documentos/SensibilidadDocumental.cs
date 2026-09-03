namespace CaeManager.Domain.Documentos;

/// <summary>
/// <b>¿Qué revela este tipo de documento sobre una persona física?</b> —
/// clasificación canónica compartida por la purga de derivados de IA y la
/// auditoría de acceso a documentos sensibles (DEC-34/36, REC-132), para que
/// ninguna de las dos mantenga su propia lista.
///
/// <para>
/// La categoría <see cref="CategoriaEspecialSalud"/> cubre <b>cualquier
/// documento o dato derivado que revele información sobre salud física o
/// mental</b> de una persona identificada — no se determina por el nombre del
/// tipo documental, sino por lo que su contenido revela (instrucción literal
/// del propietario, acta DEC-33-36). El nombre del tipo es un indicio para
/// clasificar la PROPUESTA del catálogo semilla
/// (<see cref="Infrastructure.Persistence.Seed.TipoDocumentoSeedData"/>, fuera
/// de este ensamblado), nunca la regla en sí — ver el ratchet que prohíbe
/// decidir sensibilidad comparando <see cref="TipoDocumento.Nombre"/> fuera de
/// ese único punto.
/// </para>
///
/// <para>
/// Tres valores, de menor a mayor protección exigida. <b>No se publica por
/// API todavía</b> (no hay consumidor externo en este incremento: REC-036 y
/// REC-099 lo consultan desde dominio) — si en el futuro se expone, hay que
/// añadirlo a <c>OrdinalesDeEnumsPublicadosTests</c> antes de esa exposición,
/// no después.
/// </para>
/// </summary>
public enum SensibilidadDocumental
{
    /// <summary>
    /// El contenido no identifica a ninguna persona física — describe una
    /// Empresa (certificación, cotización, evaluación, procedimiento) o un
    /// Vehículo (ficha técnica, seguro, ITV).
    /// </summary>
    SinDatosPersonales = 0,

    /// <summary>
    /// El contenido identifica a una persona física concreta (nombre, DNI,
    /// firma, listado nominal) sin revelar información sobre su salud —
    /// cualquier documento de ámbito Trabajador que no sea de la categoría
    /// especial, o un documento de Empresa cuyo contenido nombra a alguien
    /// (una designación, un acta, el registro retributivo).
    /// </summary>
    DatosPersonales = 1,

    /// <summary>
    /// Revela información sobre la salud física o mental de una persona
    /// identificada — reconocimientos médicos, resultados de aptitud, y
    /// cualquier documento o derivado equivalente, sea cual sea su nombre.
    /// <b>Valor por defecto</b> para todo tipo que no tenga una clasificación
    /// propuesta explícita: es la lectura más protectora, y la única segura
    /// cuando todavía no se ha revisado si el tipo revela salud o no —
    /// sub-clasificar (dejar pasar un tipo que sí revela salud) es el fallo
    /// que este valor por defecto existe para impedir; sobre-clasificar un
    /// tipo que no lo es se corrige en la revisión del propietario.
    /// </summary>
    CategoriaEspecialSalud = 2,
}
