using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>Fake en memoria — los handlers/servicios de Application se prueban sin base de datos (ver CODING_STANDARDS.md).</summary>
public class LineaWhatsAppRepositorioFalso : ILineaWhatsAppRepository
{
    public List<LineaWhatsApp> Lineas { get; } = [];

    public Task<LineaWhatsApp?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Lineas.FirstOrDefault(l => l.Id == id));

    public Task<LineaWhatsApp?> ObtenerPorConexionAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Lineas.FirstOrDefault(l => l.ConexionIntegracionId == conexionIntegracionId));

    public void Agregar(LineaWhatsApp linea) => Lineas.Add(linea);
}
