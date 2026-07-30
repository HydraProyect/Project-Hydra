using CaeManager.Domain.Common;

namespace CaeManager.Domain.Incidencias;

public enum TipoIncidencia
{
    Accidente,
    Incumplimiento
}

public enum GravedadIncidencia
{
    Leve,
    Grave,
    MuyGrave
}

/// <summary>
/// Accidente o incumplimiento operativo registrado en un Centro — clasificación
/// leve/grave/muy grave, la misma que usa la normativa española de PRL para
/// accidentes de trabajo.
/// </summary>
public class Incidencia : EntidadBase
{
    public const int LongitudMaximaDescripcion = 2000;

    public Guid CentroId { get; private set; }
    public Guid? TrabajadorId { get; private set; }
    public TipoIncidencia Tipo { get; private set; }
    public GravedadIncidencia Gravedad { get; private set; }
    public DateOnly FechaOcurrencia { get; private set; }
    public string Descripcion { get; private set; } = string.Empty;
    public bool Resuelta { get; private set; }
    public DateTime? ResueltaEnUtc { get; private set; }

    private Incidencia()
    {
        // Requerido por EF Core.
    }

    public Incidencia(
        Guid centroId, Guid? trabajadorId, TipoIncidencia tipo, GravedadIncidencia gravedad,
        DateOnly fechaOcurrencia, string descripcion)
    {
        if (centroId == Guid.Empty)
            throw new ArgumentException("La incidencia debe tener un centro.", nameof(centroId));

        CentroId = centroId;
        TrabajadorId = trabajadorId;
        Tipo = tipo;
        Gravedad = gravedad;
        FechaOcurrencia = fechaOcurrencia;
        EstablecerDescripcion(descripcion);
    }

    public void Actualizar(
        Guid? trabajadorId, TipoIncidencia tipo, GravedadIncidencia gravedad,
        DateOnly fechaOcurrencia, string descripcion)
    {
        TrabajadorId = trabajadorId;
        Tipo = tipo;
        Gravedad = gravedad;
        FechaOcurrencia = fechaOcurrencia;
        EstablecerDescripcion(descripcion);
    }

    public void MarcarResuelta()
    {
        if (Resuelta) return;
        Resuelta = true;
        ResueltaEnUtc = DateTime.UtcNow;
    }

    public void Reabrir()
    {
        Resuelta = false;
        ResueltaEnUtc = null;
    }

    private void EstablecerDescripcion(string descripcion)
    {
        if (string.IsNullOrWhiteSpace(descripcion))
            throw new ArgumentException("La incidencia debe tener una descripción.", nameof(descripcion));

        var normalizada = descripcion.Trim();

        if (normalizada.Length > LongitudMaximaDescripcion)
            throw new ArgumentException(
                $"La descripción no puede superar {LongitudMaximaDescripcion} caracteres.", nameof(descripcion));

        Descripcion = normalizada;
    }
}
