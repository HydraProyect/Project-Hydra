namespace CaeManager.Domain.Centros;

/// <summary>
/// Estado de cumplimiento documental de un Centro. Nunca se persiste: se
/// calcula en cada consulta a partir de los Documentos de su Empresa y de
/// cada Trabajador con Asignación activa a él (ver CalculadoraEstadoCentro).
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
    /// RETIRADO: ningún cálculo lo produce. «Bloqueado» es un estado del Trabajador, nunca del Centro: un documento ausente o
    /// vencido, y también el veredicto de la plataforma del Cliente empresarial (acreditación vencida allí o rechazada por ella,
    /// D-7 del piloto Outbound), bloquean a PERSONAS (<see cref="Documentos.ReglaBloqueoDeAcceso"/>, por Centro) y el Centro
    /// enseña el detalle por Trabajador (decisiones del propietario, 2026-10-03 y 2026-10-04). El valor se conserva porque el
    /// ordinal está publicado (<c>OrdinalesDeEnumsPublicadosTests</c>): quitarlo es una decisión aparte.
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
