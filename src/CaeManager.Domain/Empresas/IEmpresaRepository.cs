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

    /// <summary>
    /// F3b-Subcontrata — reemplaza a
    /// <c>ISubcontrataRepository.TieneTrabajadoresAsync</c>. Distinto de
    /// <see cref="TieneTrabajadoresAsync"/>: el FK repuntado conserva la
    /// columna <c>Trabajador.SubcontrataId</c> (no se fusiona con
    /// <c>EmpresaId</c>, mismo patrón P0-1 que el resto de F3b), así que una
    /// Empresa actuando como Subcontrata debe comprobarse por esa columna,
    /// no por <c>EmpresaId</c>.
    /// </summary>
    Task<bool> TieneTrabajadoresComoSubcontrataAsync(Guid empresaId, CancellationToken cancellationToken = default);

    void Agregar(Empresa empresa);
}
