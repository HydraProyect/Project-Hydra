using CaeManager.Domain.CalendarioLaboral;

namespace CaeManager.Application.Calendario;

/// <summary>
/// Lectura de las capas del calendario: el catálogo global de festivos oficiales (sin Tenant) y los
/// calendarios laborales de las personas, que son del Tenant de su cuenta (filtro de Tenant y RLS).
/// </summary>
public interface ICalendarioLaboralQueryContext
{
    IQueryable<FestivoOficial> FestivosOficiales { get; }
    IQueryable<CalendarioLaboralPersona> CalendariosLaboralesPersona { get; }
}
