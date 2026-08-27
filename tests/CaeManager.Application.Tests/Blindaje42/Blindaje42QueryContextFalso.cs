using CaeManager.Application.Blindaje42;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Blindaje42;

namespace CaeManager.Application.Tests.Blindaje42;

public class Blindaje42QueryContextFalso : IBlindaje42QueryContext
{
    public List<SolicitudCertificacionTgss> ListaSolicitudes { get; } = [];

    public IQueryable<SolicitudCertificacionTgss> SolicitudesCertificacionTgss =>
        new TestAsyncQueryable<SolicitudCertificacionTgss>(ListaSolicitudes.AsQueryable());
}
