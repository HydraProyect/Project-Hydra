using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Trabajadores;

namespace CaeManager.Application.Tests.Plantillas;

public class TrabajadoresQueryContextFalso : ITrabajadoresQueryContext
{
    public List<Trabajador> ListaTrabajadores { get; } = [];
    public List<DeteccionTrabajador> ListaDeteccionesTrabajador { get; } = [];

    public IQueryable<Trabajador> Trabajadores => new TestAsyncQueryable<Trabajador>(ListaTrabajadores.AsQueryable());
    public IQueryable<DeteccionTrabajador> DeteccionesTrabajador => new TestAsyncQueryable<DeteccionTrabajador>(ListaDeteccionesTrabajador.AsQueryable());
}
