using CaeManager.Domain.Common;

namespace CaeManager.Domain.Proyectos;

/// <summary>
/// Relación entre un Proyecto y un Trabajador ("técnico") que presta
/// servicio en él, con fecha de alta/baja — mismo patrón que
/// <see cref="Asignaciones.Asignacion"/> (Trabajador↔Centro). No todo
/// Trabajador con Asignación activa al Centro de un Proyecto trabaja
/// necesariamente en esa obra concreta, de ahí la relación explícita:
/// alimenta la facturación "por técnico" (<see cref="Facturacion.ConceptoFacturable.TecnicoAsignadoProyecto"/>).
/// </summary>
public class ProyectoTecnico : EntidadConTenant
{
    public Guid ProyectoId { get; private set; }
    public Guid TrabajadorId { get; private set; }
    public DateOnly FechaAlta { get; private set; }
    public DateOnly? FechaBaja { get; private set; }

    public bool EstaActivo => FechaBaja is null;

    private ProyectoTecnico()
    {
    }

    public ProyectoTecnico(Guid proyectoId, Guid trabajadorId, DateOnly fechaAlta)
    {
        if (proyectoId == Guid.Empty)
            throw new ArgumentException("La asignación de técnico debe tener un proyecto.", nameof(proyectoId));
        if (trabajadorId == Guid.Empty)
            throw new ArgumentException("La asignación de técnico debe tener un trabajador.", nameof(trabajadorId));

        ProyectoId = proyectoId;
        TrabajadorId = trabajadorId;
        FechaAlta = fechaAlta;
    }

    public void DarDeBaja(DateOnly fechaBaja)
    {
        if (fechaBaja < FechaAlta)
            throw new ArgumentException("La fecha de baja no puede ser anterior a la fecha de alta.", nameof(fechaBaja));

        FechaBaja = fechaBaja;
    }

    public void ReactivarAlta()
    {
        FechaBaja = null;
    }
}
