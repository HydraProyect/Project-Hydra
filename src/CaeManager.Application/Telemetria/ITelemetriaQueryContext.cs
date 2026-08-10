using CaeManager.Domain.Telemetria;

namespace CaeManager.Application.Telemetria;

public interface ITelemetriaQueryContext
{
    IQueryable<RegistroTiempoGestion> RegistrosTiempoGestion { get; }
}
