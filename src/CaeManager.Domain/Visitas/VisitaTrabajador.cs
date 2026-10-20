using CaeManager.Domain.Common;

namespace CaeManager.Domain.Visitas;

/// <summary>
/// Trabajador incluido en una Visita — mismo patrón que SubcontrataCliente:
/// sin ciclo de vida propio, desvincular es una baja física, no un soft
/// delete. Una Visita puede implicar a varios Trabajadores (un equipo que
/// entra junto al Centro).
/// </summary>
public class VisitaTrabajador : EntidadConTenant
{
    public Guid VisitaId { get; private set; }
    public Guid TrabajadorId { get; private set; }

    /// <summary>
    /// Cuándo se asignó el Trabajador a la Visita: la «fecha de solicitud» de la exportación de Trabajadores
    /// extranjeros del mes (decisión de Chris, 2026-10-20). <c>null</c> en las filas anteriores a esta columna.
    /// </summary>
    public DateTime? FechaAsignacionUtc { get; private set; }

    /// <summary>
    /// La persona que asignó el Trabajador a la Visita: el Actor real (<c>ActorAuditoria.ActorRealUsuarioId</c>), nunca
    /// el Usuario simulado de una impersonación ni el principal de la Asignación de Cartera. Es el «Gestor CAE que lo
    /// gestionó» de la exportación de Trabajadores extranjeros del mes. <c>null</c> en las filas anteriores a esta
    /// columna y en las altas sin identidad resuelta (procesos de fondo, siembras).
    /// </summary>
    public Guid? GestionadoPorUsuarioId { get; private set; }

    private VisitaTrabajador()
    {
    }

    public VisitaTrabajador(Guid visitaId, Guid trabajadorId, Guid? gestionadoPorUsuarioId = null, DateTime? fechaAsignacionUtc = null)
    {
        if (visitaId == Guid.Empty)
            throw new ArgumentException("La asociación debe tener una visita.", nameof(visitaId));
        if (trabajadorId == Guid.Empty)
            throw new ArgumentException("La asociación debe tener un trabajador.", nameof(trabajadorId));

        VisitaId = visitaId;
        TrabajadorId = trabajadorId;
        GestionadoPorUsuarioId = gestionadoPorUsuarioId == Guid.Empty ? null : gestionadoPorUsuarioId;
        FechaAsignacionUtc = fechaAsignacionUtc ?? DateTime.UtcNow;
    }
}
