using CaeManager.Domain.Common;

namespace CaeManager.Domain.Reportes;

/// <summary>
/// Registro append-only de cada informe generado desde Reportes — nace con
/// la vista previa (Reportes TALVEG.dc.html), no con la descarga: es lo que
/// alimenta el panel "Historial" del propio mockup. Congela el nombre del
/// cliente en el momento de generar (igual criterio que otros historiales
/// de esta app): un Cliente borrado después no debe dejar la fila en blanco.
/// </summary>
public class HistorialInforme : EntidadConTenant
{
    public string TipoInforme { get; private set; } = string.Empty;
    public Guid? ClienteId { get; private set; }
    public string? ClienteNombre { get; private set; }
    public DateTime GeneradoEnUtc { get; private set; }
    public Guid GeneradoPorUsuarioId { get; private set; }

    private HistorialInforme()
    {
    }

    public HistorialInforme(string tipoInforme, Guid? clienteId, string? clienteNombre, Guid generadoPorUsuarioId)
    {
        if (string.IsNullOrWhiteSpace(tipoInforme))
            throw new ArgumentException("El informe debe tener un tipo.", nameof(tipoInforme));

        TipoInforme = tipoInforme;
        ClienteId = clienteId;
        ClienteNombre = clienteNombre;
        GeneradoEnUtc = DateTime.UtcNow;
        GeneradoPorUsuarioId = generadoPorUsuarioId;
    }
}
