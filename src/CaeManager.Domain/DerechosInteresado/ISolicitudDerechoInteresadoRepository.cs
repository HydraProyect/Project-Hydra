namespace CaeManager.Domain.DerechosInteresado;

/// <summary>
/// Solicitudes de derechos del interesado del Tenant actual (el filtro global y la RLS lo acotan; nunca se pasa el
/// Tenant como parámetro).
/// </summary>
public interface ISolicitudDerechoInteresadoRepository
{
    /// <summary>La solicitud del Tenant actual, con seguimiento, para cambiar su estado.</summary>
    Task<SolicitudDerechoInteresado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Las solicitudes del Tenant actual, sin seguimiento, de la más antigua por vencer a la que más.</summary>
    Task<IReadOnlyList<SolicitudDerechoInteresado>> ListarAsync(CancellationToken cancellationToken);

    void Agregar(SolicitudDerechoInteresado solicitud);
}
