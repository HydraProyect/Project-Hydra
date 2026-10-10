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
    /// Su valor numérico es mayor que el de <see cref="Vencido"/> y está
    /// congelado por la API v1, pero NO es más grave: con un documento vencido
    /// y otro que falta, el Centro está <see cref="Vencido"/>
    /// (<see cref="CalculadoraEstadoCentro"/>).
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
    SinGestionCae = 6,

    /// <summary>
    /// Pendiente en la plataforma CAE del Centro (decisión del propietario, 2026-10-10): un documento que todavía vale en
    /// TALVEG está, en la plataforma de este Centro, sin subir o subido y sin validar
    /// (<see cref="Documentos.ReglaPendienteEnPlataforma"/>). Solo lo causa un Centro con plataforma: uno sin acceso de
    /// tipo Plataforma nunca está Pendiente. En gravedad va entre <see cref="Faltante"/> y <see cref="Urgente"/>
    /// (<see cref="CalculadoraEstadoCentro.Gravedad"/>); su valor numérico va al final porque los anteriores están
    /// congelados por la API v1. No pone el Centro en <see cref="Bloqueado"/>: el Pendiente bloquea a PERSONAS en ese
    /// Centro (al Trabajador del documento, o a todos los de la Empresa si el documento es de Empresa), y solo si el Centro
    /// marca el tipo como bloqueante (<see cref="Documentos.TipoDocumentoCentro.BloqueaAcceso"/>).
    /// </summary>
    Pendiente = 7
}
