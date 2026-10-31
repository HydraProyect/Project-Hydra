namespace CaeManager.Domain.Integraciones;

/// <summary>Los interruptores de una plataforma, leídos de la base en cada operación y nunca de una entidad ya seguida.</summary>
public sealed record InterruptoresConectorProveedor(string Codigo, bool Activo, bool ConectorApiActivo);

public interface IProveedorPlataformaCaeRepository
{
    Task<InterruptoresConectorProveedor?> ObtenerInterruptoresConectorAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(ProveedorPlataformaCae proveedor);

    void AgregarDominio(DominioProveedorPlataformaCae dominio);

    Task<ProveedorPlataformaCae?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);
}
