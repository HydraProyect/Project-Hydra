namespace CaeManager.Domain.Empresas;

public interface IEmpresaClienteRepository
{
    Task<IReadOnlyList<EmpresaCliente>> ObtenerPorEmpresaAsync(Guid empresaId, CancellationToken cancellationToken = default);

    void Agregar(EmpresaCliente empresaCliente);

    void Eliminar(EmpresaCliente empresaCliente);
}
