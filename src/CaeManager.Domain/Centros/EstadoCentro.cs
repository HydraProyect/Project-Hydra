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
    /// Solo lo causa la plataforma del Cliente empresarial: una acreditación vencida allí o rechazada por ella (D-7 del piloto
    /// Outbound). Es el peor caso posible. Un documento de Trabajador o de Empresa ausente o vencido NO lo causa: bloquea a
    /// PERSONAS (<see cref="Documentos.ReglaBloqueoDeAcceso"/>, por Centro) y el Centro enseña el detalle por Trabajador; «Bloqueado»
    /// es un estado del Trabajador (decisión del propietario, 2026-10-03). Que el veredicto de la plataforma siga marcando el
    /// Centro es una decisión pendiente aparte.
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
