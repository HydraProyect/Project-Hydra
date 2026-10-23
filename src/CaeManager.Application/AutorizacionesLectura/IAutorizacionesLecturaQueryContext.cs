using CaeManager.Domain.AutorizacionesLectura;

namespace CaeManager.Application.AutorizacionesLectura;

public interface IAutorizacionesLecturaQueryContext
{
    IQueryable<AutorizacionLecturaDocumental> AutorizacionesLecturaDocumental { get; }

    IQueryable<AutorizacionLecturaTrabajador> AutorizacionesLecturaTrabajadores { get; }
}
