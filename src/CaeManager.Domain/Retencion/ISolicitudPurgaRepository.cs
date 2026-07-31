namespace CaeManager.Domain.Retencion;

public interface ISolicitudPurgaRepository
{
    Task<SolicitudPurga?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Si ya hay una propuesta sin resolver para esa categoría — pendiente de
    /// revisión, avisada o programada. Evita que repetir el barrido llene la
    /// bandeja de duplicados de lo mismo.
    /// </summary>
    Task<bool> ExisteAbiertaAsync(TipoDatoPurgable tipoDato, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SolicitudPurga>> ObtenerPendientesAsync(CancellationToken cancellationToken = default);

    /// <summary>Las autorizadas cuya fecha ya llegó: lo único que puede ejecutarse.</summary>
    Task<IReadOnlyList<SolicitudPurga>> ObtenerEjecutablesAsync(DateOnly hoy, CancellationToken cancellationToken = default);

    void Agregar(SolicitudPurga solicitud);
}
