using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;

namespace CaeManager.Application.Empresas;

public interface IEmpresasQueryContext
{
    IQueryable<Empresa> Empresas { get; }
    IQueryable<EmpresaCliente> EmpresasClientes { get; }
    IQueryable<CredencialAccesoEmpresa> CredencialesAccesoEmpresa { get; }

    /// <summary>
    /// F4.2b — expuesto para que los lectores de Application puedan migrar
    /// de las tres tablas puente legacy (<see cref="EmpresaCliente"/> y las
    /// de <c>Domain.Subcontratas</c>) a la arista unificada. Filtrar siempre
    /// por <c>VigenciaHasta == null</c>: a diferencia del borrado físico de
    /// las tablas legacy, una relación cerrada no desaparece.
    /// </summary>
    IQueryable<RelacionEmpresarial> RelacionesEmpresariales { get; }
}
