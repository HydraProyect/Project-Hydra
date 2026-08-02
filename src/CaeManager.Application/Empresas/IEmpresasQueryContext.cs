using CaeManager.Domain.Empresas;

namespace CaeManager.Application.Empresas;

public interface IEmpresasQueryContext
{
    IQueryable<Empresa> Empresas { get; }
    IQueryable<EmpresaCliente> EmpresasClientes { get; }
    IQueryable<CredencialAccesoEmpresa> CredencialesAccesoEmpresa { get; }
}
