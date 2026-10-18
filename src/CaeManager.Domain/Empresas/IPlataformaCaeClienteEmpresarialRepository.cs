namespace CaeManager.Domain.Empresas;

/// <summary>Escritura de la caja «Plataforma CAE» de Cliente 360: la plataforma del Cliente empresarial y los accesos de cada Empresa.</summary>
public interface IPlataformaCaeClienteEmpresarialRepository
{
    Task<PlataformaCaeClienteEmpresarial?> ObtenerPlataformaAsync(Guid clienteEmpresarialId, CancellationToken cancellationToken = default);

    void AgregarPlataforma(PlataformaCaeClienteEmpresarial plataforma);

    Task<AccesoPlataformaCaeEmpresa?> ObtenerAccesoAsync(Guid clienteEmpresarialId, Guid empresaId, CancellationToken cancellationToken = default);

    void AgregarAcceso(AccesoPlataformaCaeEmpresa acceso);
}
