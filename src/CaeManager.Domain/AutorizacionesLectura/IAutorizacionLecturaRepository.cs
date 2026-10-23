namespace CaeManager.Domain.AutorizacionesLectura;

/// <summary>Sin borrado de la autorización: se revoca. Las filas de Trabajadores sí se borran mientras no esté revocada.</summary>
public interface IAutorizacionLecturaRepository
{
    Task<AutorizacionLecturaDocumental?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AutorizacionLecturaTrabajador>> ObtenerTrabajadoresAsync(
        Guid autorizacionId, CancellationToken cancellationToken = default);

    /// <summary>¿Hay ya una autorización sin revocar para ese Tenant destinatario y ese contrato?</summary>
    Task<bool> ExisteNoRevocadaAsync(Guid tenantDestinatarioId, Guid contratoAccesoId, CancellationToken cancellationToken = default);

    void Agregar(AutorizacionLecturaDocumental autorizacion);

    void AgregarTrabajador(AutorizacionLecturaTrabajador trabajador);

    void RetirarTrabajador(AutorizacionLecturaTrabajador trabajador);
}
