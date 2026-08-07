namespace CaeManager.Domain.Integraciones;

public interface IProveedorPlataformaCaeRepository
{
    void Agregar(ProveedorPlataformaCae proveedor);

    void AgregarDominio(DominioProveedorPlataformaCae dominio);
}
