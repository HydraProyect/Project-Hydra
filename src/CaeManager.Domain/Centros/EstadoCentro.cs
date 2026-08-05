namespace CaeManager.Domain.Centros;

/// <summary>
/// Estado de cumplimiento documental de un Centro. Nunca se persiste: se
/// calcula en cada consulta a partir de los Documentos de su Empresa y de
/// cada Trabajador con Asignación activa a él, y de sus RequisitosDocumentales
/// bloqueantes sin cumplir (ver CalculadoraEstadoCentro).
/// </summary>
public enum EstadoCentro
{
    Vigente,
    Proximo,
    Urgente,
    Vencido,

    /// <summary>
    /// Un Trabajador con Asignación activa a este Centro no tiene ningún
    /// Documento de un TipoDocumento obligatorio que el Centro le exige.
    /// </summary>
    Faltante,

    /// <summary>
    /// Al menos un RequisitoDocumental de este Centro con BloqueaAcceso=true
    /// sigue sin Cumplido. Es el peor caso posible: aunque toda la
    /// documentación esté Vigente, el acceso al Centro sigue bloqueado.
    /// </summary>
    Bloqueado
}
