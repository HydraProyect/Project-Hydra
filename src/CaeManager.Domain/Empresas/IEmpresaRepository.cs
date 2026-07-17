namespace CaeManager.Domain.Empresas;

public interface IEmpresaRepository
{
    Task<Empresa?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteConRazonSocialAsync(string razonSocial, Guid? excluirId = null, CancellationToken cancellationToken = default);

    Task<bool> ExisteConCifAsync(string cif, Guid? excluirId = null, CancellationToken cancellationToken = default);

    /// <summary>Una Empresa con Trabajadores no puede eliminarse (ver EliminarEmpresaCommand).</summary>
    Task<bool> TieneTrabajadoresAsync(Guid empresaId, CancellationToken cancellationToken = default);

    void Agregar(Empresa empresa);
}
