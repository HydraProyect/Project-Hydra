using CaeManager.Application.Centros;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Centros;

namespace CaeManager.Application.Tests.Plantillas;

public class CentrosQueryContextFalso : ICentrosQueryContext
{
    public List<Centro> ListaCentros { get; } = [];
    public List<CanalGestionDocumental> ListaCanalesGestionDocumental { get; } = [];

    public IQueryable<Centro> Centros => new TestAsyncQueryable<Centro>(ListaCentros.AsQueryable());
    public IQueryable<CanalGestionDocumental> CanalesGestionDocumental => new TestAsyncQueryable<CanalGestionDocumental>(ListaCanalesGestionDocumental.AsQueryable());
}
