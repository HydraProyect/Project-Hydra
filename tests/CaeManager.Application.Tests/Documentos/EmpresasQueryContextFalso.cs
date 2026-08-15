using CaeManager.Application.Empresas;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Empresas;

namespace CaeManager.Application.Tests.Documentos;

public class EmpresasQueryContextFalso : IEmpresasQueryContext
{
    public List<Empresa> ListaEmpresas { get; } = [];
    public List<EmpresaCliente> ListaEmpresasClientes { get; } = [];
    public List<CredencialAccesoEmpresa> ListaCredencialesAccesoEmpresa { get; } = [];

    public IQueryable<Empresa> Empresas => new TestAsyncQueryable<Empresa>(ListaEmpresas.AsQueryable());
    public IQueryable<EmpresaCliente> EmpresasClientes => new TestAsyncQueryable<EmpresaCliente>(ListaEmpresasClientes.AsQueryable());
    public IQueryable<CredencialAccesoEmpresa> CredencialesAccesoEmpresa => new TestAsyncQueryable<CredencialAccesoEmpresa>(ListaCredencialesAccesoEmpresa.AsQueryable());
}
