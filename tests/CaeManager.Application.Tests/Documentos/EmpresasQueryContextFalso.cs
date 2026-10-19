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
    public List<PlataformaCaeClienteEmpresarial> ListaPlataformasCae { get; } = [];
    public List<AccesoPlataformaCaeEmpresa> ListaAccesosPlataformaCae { get; } = [];
    public IQueryable<PlataformaCaeClienteEmpresarial> PlataformasCaeClienteEmpresarial => new TestAsyncQueryable<PlataformaCaeClienteEmpresarial>(ListaPlataformasCae.AsQueryable());
    public IQueryable<AccesoPlataformaCaeEmpresa> AccesosPlataformaCaeEmpresa => new TestAsyncQueryable<AccesoPlataformaCaeEmpresa>(ListaAccesosPlataformaCae.AsQueryable());
    public List<DelegacionEmpresa> ListaDelegacionesEmpresa { get; } = [];
    public List<OficioEmpresa> ListaOficiosEmpresa { get; } = [];
    public IQueryable<DelegacionEmpresa> DelegacionesEmpresa => new TestAsyncQueryable<DelegacionEmpresa>(ListaDelegacionesEmpresa.AsQueryable());
    public IQueryable<OficioEmpresa> OficiosEmpresa => new TestAsyncQueryable<OficioEmpresa>(ListaOficiosEmpresa.AsQueryable());
}
