using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>Fake en memoria — los handlers de Application se prueban sin base de datos (ver CODING_STANDARDS.md).</summary>
public class ConversacionRepositorioFalso : IConversacionRepository
{
    public List<Conversacion> Conversaciones { get; } = [];

    public Task<Conversacion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Conversaciones.FirstOrDefault(c => c.Id == id));

    public Task<Conversacion?> ObtenerPorHiloExternoAsync(
        Guid conexionIntegracionId, string hiloExternoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Conversaciones.FirstOrDefault(
            c => c.ConexionIntegracionId == conexionIntegracionId && c.HiloExternoId == hiloExternoId));

    public Task<bool> ExisteMensajeExternoAsync(Guid conexionIntegracionId, string mensajeExternoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Conversaciones
            .Where(c => c.ConexionIntegracionId == conexionIntegracionId)
            .SelectMany(c => c.Mensajes)
            .Any(m => m.MensajeExternoId == mensajeExternoId));

    public Task<Conversacion?> ObtenerAbiertaPorTelefonoAsync(
        Guid conexionIntegracionId, string telefonoContacto, CancellationToken cancellationToken = default) =>
        Task.FromResult(Conversaciones
            .Where(c => c.Canal == CanalConversacion.WhatsApp
                        && c.ConexionIntegracionId == conexionIntegracionId
                        && c.TelefonoContacto == telefonoContacto
                        && c.Estado != EstadoConversacion.Cerrada)
            .OrderByDescending(c => c.FechaUltimoMensajeUtc)
            .FirstOrDefault());

    public Task<Mensaje?> ObtenerMensajePorExternoIdAsync(Guid conexionIntegracionId, string mensajeExternoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Conversaciones
            .Where(c => c.ConexionIntegracionId == conexionIntegracionId)
            .SelectMany(c => c.Mensajes)
            .FirstOrDefault(m => m.MensajeExternoId == mensajeExternoId));

    public Task<IReadOnlyDictionary<Guid, int>> ContarWhatsAppVivasPorEjecutivoAsync(
        IReadOnlyCollection<Guid> ejecutivoIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, int>>(Conversaciones
            .Where(c => c.Canal == CanalConversacion.WhatsApp
                        && (c.Estado == EstadoConversacion.Abierta || c.Estado == EstadoConversacion.Pendiente)
                        && c.EjecutivoAsignadoId is { } id && ejecutivoIds.Contains(id))
            .GroupBy(c => c.EjecutivoAsignadoId!.Value)
            .ToDictionary(g => g.Key, g => g.Count()));

    public Task<IReadOnlyList<Conversacion>> ObtenerAbiertasPorClienteAsync(
        Guid clienteId, Guid excluirConversacionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Conversacion>>(Conversaciones
            .Where(c => c.ClienteId == clienteId && c.Id != excluirConversacionId
                        && (c.Estado == EstadoConversacion.Abierta || c.Estado == EstadoConversacion.Pendiente))
            .OrderByDescending(c => c.FechaUltimoMensajeUtc)
            .ToList());

    public void Agregar(Conversacion conversacion) => Conversaciones.Add(conversacion);
}
