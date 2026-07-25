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

    private VisitaTrabajador()
    {
    }

    public VisitaTrabajador(Guid visitaId, Guid trabajadorId)
    {
        if (visitaId == Guid.Empty)
            throw new ArgumentException("La asociación debe tener una visita.", nameof(visitaId));
        if (trabajadorId == Guid.Empty)
            throw new ArgumentException("La asociación debe tener un trabajador.", nameof(trabajadorId));

        VisitaId = visitaId;
        TrabajadorId = trabajadorId;
    }
}
