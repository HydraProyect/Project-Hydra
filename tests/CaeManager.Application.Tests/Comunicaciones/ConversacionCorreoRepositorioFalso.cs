using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>Fake en memoria — los handlers de Application se prueban sin base de datos (ver CODING_STANDARDS.md).</summary>
public class ConversacionCorreoRepositorioFalso : IConversacionCorreoRepository
{
    public List<ConversacionCorreo> Conversaciones { get; } = [];

    public Task<ConversacionCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Conversaciones.FirstOrDefault(c => c.Id == id));

    public Task<ConversacionCorreo?> ObtenerPorHiloExternoAsync(string hiloExternoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Conversaciones.FirstOrDefault(c => c.HiloExternoId == hiloExternoId));

    public Task<bool> ExisteMensajeExternoAsync(string mensajeExternoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Conversaciones.SelectMany(c => c.Mensajes).Any(m => m.MensajeExternoId == mensajeExternoId));

    public void Agregar(ConversacionCorreo conversacion) => Conversaciones.Add(conversacion);
}
