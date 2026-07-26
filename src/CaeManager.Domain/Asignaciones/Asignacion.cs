using CaeManager.Domain.Common;

namespace CaeManager.Domain.Asignaciones;

/// <summary>
/// Relación entre un Trabajador y un Centro, con fecha de alta/baja. Mejora
/// directa sobre la matriz de "X" del Excel original, que no guardaba
/// historial de fechas (ver DATABASE.md).
/// </summary>
public class Asignacion : EntidadConTenant
{
    public Guid TrabajadorId { get; private set; }
    public Guid CentroId { get; private set; }
    public DateOnly FechaAlta { get; private set; }
    public DateOnly? FechaBaja { get; private set; }

    public bool EstaActiva => FechaBaja is null;

    private Asignacion()
    {
    }

    public Asignacion(Guid trabajadorId, Guid centroId, DateOnly fechaAlta)
    {
        if (trabajadorId == Guid.Empty)
            throw new ArgumentException("La asignación debe tener un trabajador.", nameof(trabajadorId));
        if (centroId == Guid.Empty)
            throw new ArgumentException("La asignación debe tener un centro.", nameof(centroId));

        TrabajadorId = trabajadorId;
        CentroId = centroId;
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
