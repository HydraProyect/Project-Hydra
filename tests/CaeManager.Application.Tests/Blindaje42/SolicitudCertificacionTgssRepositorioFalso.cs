using CaeManager.Domain.Blindaje42;

namespace CaeManager.Application.Tests.Blindaje42;

public class SolicitudCertificacionTgssRepositorioFalso : ISolicitudCertificacionTgssRepository
{
    public List<SolicitudCertificacionTgss> Solicitudes { get; } = [];

    public Task<SolicitudCertificacionTgss?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Solicitudes.FirstOrDefault(s => s.Id == id));

    public void Agregar(SolicitudCertificacionTgss solicitud) => Solicitudes.Add(solicitud);
}
