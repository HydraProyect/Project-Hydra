namespace CaeManager.Domain.Comunicaciones;

public interface IUltimoResumenNotificacionPlataformaRepository
{
    Task<UltimoResumenNotificacionPlataforma?> ObtenerAsync(
        Guid clienteId, Guid proveedorPlataformaCaeId, CancellationToken cancellationToken = default);

    void Agregar(UltimoResumenNotificacionPlataforma resumen);
}
