using CaeManager.Domain.Blindaje42;

namespace CaeManager.Application.Blindaje42;

public interface IBlindaje42QueryContext
{
    IQueryable<SolicitudCertificacionTgss> SolicitudesCertificacionTgss { get; }
}
