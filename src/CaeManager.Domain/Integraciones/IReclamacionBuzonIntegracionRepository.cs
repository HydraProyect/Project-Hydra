namespace CaeManager.Domain.Integraciones;

public interface IReclamacionBuzonIntegracionRepository
{
    void Reclamar(ReclamacionBuzonIntegracion reclamacion);

    /// <summary>
    /// Persiste el Unit of Work compartido (la reclamación y, en el mismo
    /// <c>SaveChangesAsync</c>, cualquier otro cambio pendiente del mismo
    /// contexto — p. ej. la <c>ConexionIntegracion</c>/<c>CredencialIntegracion</c>/
    /// <c>SuscripcionWebhook</c> recién creadas). Si el <see cref="ReclamacionBuzonIntegracion.BuzonEmail"/>
    /// ya está reclamado por otro Tenant o conexión, el índice único lo
    /// rechaza a nivel de PostgreSQL: se traduce a <c>false</c> (y se
    /// descartan los cambios en memoria) en vez de propagar la excepción —
    /// mismo patrón que <c>OperacionImportacionRepository.GuardarSiOperacionNuevaAsync</c>.
    /// </summary>
    Task<bool> GuardarCambiosSiBuzonLibreAsync(CancellationToken cancellationToken = default);

    /// <summary>Libera la reclamación de una conexión desconectada — deja el buzón disponible para cualquier Tenant.</summary>
    Task LiberarAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default);
}
