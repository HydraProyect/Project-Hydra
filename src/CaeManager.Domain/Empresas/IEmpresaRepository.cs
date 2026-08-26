namespace CaeManager.Domain.Empresas;

public interface IEmpresaRepository
{
    Task<Empresa?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteConRazonSocialAsync(string razonSocial, Guid? excluirId = null, CancellationToken cancellationToken = default);

    Task<bool> ExisteConCifAsync(string cif, Guid? excluirId = null, CancellationToken cancellationToken = default);

    /// <summary>Una Empresa con Trabajadores no puede eliminarse (ver EliminarEmpresaCommand).</summary>
    Task<bool> TieneTrabajadoresAsync(Guid empresaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// F3b — reemplaza a <c>IClienteRepository.TieneCentrosActivosAsync</c>:
    /// una Empresa actuando como Cliente (titular) con Centros activos no
    /// puede eliminarse (ver EliminarClienteCommand).
    /// </summary>
    Task<bool> TieneCentrosComoTitularAsync(Guid empresaId, CancellationToken cancellationToken = default);

    void Agregar(Empresa empresa);
}
