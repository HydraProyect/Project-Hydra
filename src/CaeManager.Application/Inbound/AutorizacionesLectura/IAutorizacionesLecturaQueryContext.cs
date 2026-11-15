using CaeManager.Domain.Inbound.AutorizacionesLectura;

namespace CaeManager.Application.Inbound.AutorizacionesLectura;

public interface IAutorizacionesLecturaQueryContext
{
    IQueryable<AutorizacionLecturaDocumental> AutorizacionesLecturaDocumental { get; }

    IQueryable<AutorizacionLecturaTrabajador> AutorizacionesLecturaTrabajadores { get; }
}
