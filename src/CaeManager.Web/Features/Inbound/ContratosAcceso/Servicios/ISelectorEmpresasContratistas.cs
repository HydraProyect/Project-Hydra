namespace CaeManager.Web.Features.Inbound.ContratosAcceso.Servicios;

/// <summary>Una Empresa contratista elegible en el alta de un contrato de acceso: la Empresa contraparte local del Tenant.</summary>
public sealed record EmpresaContratistaOpcion(Guid Id, string RazonSocial);

/// <summary>
/// Las Empresas contratistas entre las que se elige al dar de alta un contrato de acceso. Hoy no tiene backend en el Núcleo:
/// el único selector de contrapartes (<c>ObtenerSubcontratasParaSelectorQuery</c>) es de Outbound y Inbound no puede depender
/// de él (arista cerrada Inbound a Outbound, ADR-013 § 4.3), y <c>ObtenerEmpresasParaSelectorQuery</c> solo ofrece Empresas propias.
/// </summary>
public interface ISelectorEmpresasContratistas
{
    Task<IReadOnlyList<EmpresaContratistaOpcion>> ListarAsync(CancellationToken cancellationToken = default);
}

/// <summary>Implementación vacía mientras no exista un selector de Empresas contraparte en el Núcleo.</summary>
public sealed class SelectorEmpresasContratistasNoDisponible : ISelectorEmpresasContratistas
{
    // shortcut: sin backend, cablear cuando entren B-06 (alta de una Empresa contratista) y B-07 (cotejo por CIF), o una Query de
    // Núcleo que liste las Empresas contraparte del alcance de gestión. DECISIÓN NECESARIA de la coordinadora.
    public Task<IReadOnlyList<EmpresaContratistaOpcion>> ListarAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EmpresaContratistaOpcion>>([]);
}
