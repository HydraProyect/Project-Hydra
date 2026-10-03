namespace CaeManager.Domain.Centros;

/// <summary>
/// Estado de cumplimiento documental de un Centro. Nunca se persiste: se
/// calcula en cada consulta a partir de los Documentos de su Empresa y de
/// cada Trabajador con Asignación activa a él, y de sus RequisitosDocumentales
/// bloqueantes sin cumplir (ver CalculadoraEstadoCentro).
/// </summary>
public enum EstadoCentro
{
    Vigente = 0,
    Proximo = 1,
    Urgente = 2,
    Vencido = 3,

    /// <summary>
    /// Un Trabajador con Asignación activa a este Centro no tiene ningún
    /// Documento de un TipoDocumento obligatorio que el Centro le exige.
    /// </summary>
    Faltante = 4,

    /// <summary>
    /// Hoy lo causa un Trabajador asignado al que le falta por completo un Tipo
    /// marcado con <c>TipoDocumentoCentro.BloqueaAcceso</c>, o una acreditación de
    /// la plataforma vencida o rechazada. Es el peor caso posible. OJO: la regla
    /// de acceso (<see cref="Documentos.ReglaBloqueoDeAcceso"/>: ausente o vencido,
    /// del Trabajador o de su Empresa, bloquean a PERSONAS, no al Centro) no está
    /// aplicada entera aquí; qué debe enseñar el Centro con Trabajadores o Empresas
    /// bloqueados es una decisión de producto pendiente.
    /// </summary>
    Bloqueado = 5,

    /// <summary>
    /// El Centro no requiere gestión CAE (<see cref="ModalidadGestionCae.SinGestionCae"/>):
    /// no exige documentación, así que no hay nada que pueda estar al día ni
    /// fallar. No es <see cref="Vigente"/> a propósito — un verde ahí afirmaría
    /// un cumplimiento que nadie ha comprobado. No es un grado de gravedad: al
    /// ordenar por gravedad va antes que <see cref="Vigente"/>
    /// (<see cref="CalculadoraEstadoCentro.Gravedad"/>).
    /// </summary>
    SinGestionCae = 6
}
