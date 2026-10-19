namespace CaeManager.Domain.Empresas;

/// <summary>Escritura de la caja «Plataforma CAE» de Cliente 360: la plataforma del Cliente empresarial y los accesos de cada Empresa.</summary>
public interface IPlataformaCaeClienteEmpresarialRepository
{
    Task<PlataformaCaeClienteEmpresarial?> ObtenerPlataformaAsync(Guid clienteEmpresarialId, CancellationToken cancellationToken = default);

    void AgregarPlataforma(PlataformaCaeClienteEmpresarial plataforma);

    Task<AccesoPlataformaCaeEmpresa?> ObtenerAccesoAsync(Guid clienteEmpresarialId, Guid empresaId, CancellationToken cancellationToken = default);

    void AgregarAcceso(AccesoPlataformaCaeEmpresa acceso);

    /// <summary>
    /// Guarda los cambios pendientes. Devuelve <c>false</c> si otra petición dio de alta a la vez la
    /// misma plataforma o el mismo acceso (índice único por Tenant propietario, Cliente empresarial
    /// y, en el acceso, Empresa); cualquier otro error se propaga.
    /// </summary>
    Task<bool> GuardarCambiosAsync(CancellationToken cancellationToken = default);
}
