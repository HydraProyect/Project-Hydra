using CaeManager.Application.Empresas;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;

namespace CaeManager.Application.Tests.Documentos;

public class EmpresasQueryContextFalso : IEmpresasQueryContext
{
    public List<Empresa> ListaEmpresas { get; } = [];
    public List<CredencialAccesoEmpresa> ListaCredencialesAccesoEmpresa { get; } = [];
    public List<RelacionEmpresarial> ListaRelacionesEmpresariales { get; } = [];

    public IQueryable<Empresa> Empresas => new TestAsyncQueryable<Empresa>(ListaEmpresas.AsQueryable());
    public IQueryable<CredencialAccesoEmpresa> CredencialesAccesoEmpresa => new TestAsyncQueryable<CredencialAccesoEmpresa>(ListaCredencialesAccesoEmpresa.AsQueryable());
    public IQueryable<RelacionEmpresarial> RelacionesEmpresariales => new TestAsyncQueryable<RelacionEmpresarial>(ListaRelacionesEmpresariales.AsQueryable());
}
