namespace CaeManager.Domain.Integraciones;

/// <summary>Lo que respondió PostgreSQL al guardar una reclamación de buzón.</summary>
public enum ResultadoReclamacionBuzon
{
    Guardada,

    /// <summary>El índice único por dirección o por identidad: el buzón ya está reclamado por otro Tenant o conexión.</summary>
    BuzonYaReclamado,

    /// <summary>
    /// La guarda transitoria (<see cref="ReclamacionBuzonIntegracion.CuentaParaGuardaTransitoria"/>): queda alguna
    /// reclamación heredada sin identidad en la plataforma y el alta de un buzón compartido espera. No dice de quién.
    /// </summary>
    AltaCompartidaEnPausa,
}

public interface IReclamacionBuzonIntegracionRepository
{
    void Reclamar(ReclamacionBuzonIntegracion reclamacion);

    /// <summary>La reclamación de una conexión del Tenant actual (RLS), o null si no la tiene.</summary>
    Task<ReclamacionBuzonIntegracion?> ObtenerPorConexionAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Las reclamaciones del Tenant actual (RLS) que cuentan para la guarda transitoria
    /// (<see cref="ReclamacionBuzonIntegracion.CuentaParaGuardaTransitoria"/>), rastreadas para poder sacarlas de ella.
    /// </summary>
    Task<IReadOnlyList<ReclamacionBuzonIntegracion>> ObtenerQueCuentanParaGuardaTransitoriaAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persiste el Unit of Work compartido (la reclamación y, en el mismo
    /// <c>SaveChangesAsync</c>, cualquier otro cambio pendiente del mismo
    /// contexto — p. ej. la <c>ConexionIntegracion</c>/<c>CredencialIntegracion</c>/
    /// <c>SuscripcionWebhook</c> recién creadas). Si el <see cref="ReclamacionBuzonIntegracion.BuzonEmail"/>
    /// o la <see cref="ReclamacionBuzonIntegracion.IdentidadBuzon"/>
    /// ya está reclamado por otro Tenant o conexión, el índice único lo
    /// rechaza a nivel de PostgreSQL, y la guarda transitoria rechaza igual la de un buzón compartido: se traduce a
    /// <see cref="ResultadoReclamacionBuzon"/> (y se descartan los cambios en memoria) en vez de propagar la excepción —
    /// mismo patrón que <c>OperacionImportacionRepository.GuardarSiOperacionNuevaAsync</c>.
    /// </summary>
    Task<ResultadoReclamacionBuzon> GuardarCambiosSiBuzonLibreAsync(CancellationToken cancellationToken = default);

    /// <summary>Libera la reclamación de una conexión desconectada — deja el buzón disponible para cualquier Tenant.</summary>
    Task LiberarAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default);
}
