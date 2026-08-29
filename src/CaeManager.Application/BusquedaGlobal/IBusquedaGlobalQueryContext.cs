using CaeManager.Domain.BusquedaGlobal;

namespace CaeManager.Application.BusquedaGlobal;

public interface IBusquedaGlobalQueryContext
{
    IQueryable<EventoRecienteUsuario> EventosRecientesUsuario { get; }
}
