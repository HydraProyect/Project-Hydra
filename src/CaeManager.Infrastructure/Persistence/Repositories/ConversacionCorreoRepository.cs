using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ConversacionCorreoRepository(CaeManagerDbContext dbContext) : IConversacionCorreoRepository
{
    public Task<ConversacionCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ConversacionesCorreo
            .Include(c => c.Mensajes).ThenInclude(m => m.Adjuntos)
            .Include(c => c.Participantes)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<ConversacionCorreo?> ObtenerPorHiloExternoAsync(string hiloExternoId, CancellationToken cancellationToken = default) =>
        dbContext.ConversacionesCorreo
            .Include(c => c.Mensajes).ThenInclude(m => m.Adjuntos)
            .Include(c => c.Participantes)
            .FirstOrDefaultAsync(c => c.HiloExternoId == hiloExternoId, cancellationToken);

    public Task<bool> ExisteMensajeExternoAsync(string mensajeExternoId, CancellationToken cancellationToken = default) =>
        dbContext.MensajesCorreo.AnyAsync(m => m.MensajeExternoId == mensajeExternoId, cancellationToken);

    public Task<ConversacionCorreo?> ObtenerAbiertaPorTelefonoAsync(
        Guid conexionIntegracionId, string telefonoContacto, CancellationToken cancellationToken = default) =>
        dbContext.ConversacionesCorreo
            .Include(c => c.Mensajes).ThenInclude(m => m.Adjuntos)
            .Include(c => c.Participantes)
            .Where(c => c.Canal == CanalConversacion.WhatsApp
                        && c.ConexionIntegracionId == conexionIntegracionId
                        && c.TelefonoContacto == telefonoContacto
                        && c.Estado != EstadoConversacion.Cerrada)
            .OrderByDescending(c => c.FechaUltimoMensajeUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<MensajeCorreo?> ObtenerMensajePorExternoIdAsync(string mensajeExternoId, CancellationToken cancellationToken = default) =>
        dbContext.MensajesCorreo.FirstOrDefaultAsync(m => m.MensajeExternoId == mensajeExternoId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, int>> ContarWhatsAppVivasPorEjecutivoAsync(
        IReadOnlyCollection<Guid> ejecutivoIds, CancellationToken cancellationToken = default) =>
        await dbContext.ConversacionesCorreo
            .Where(c => c.Canal == CanalConversacion.WhatsApp
                        && (c.Estado == EstadoConversacion.Abierta || c.Estado == EstadoConversacion.Pendiente)
                        && c.EjecutivoAsignadoId != null && ejecutivoIds.Contains(c.EjecutivoAsignadoId.Value))
            .GroupBy(c => c.EjecutivoAsignadoId!.Value)
            .Select(g => new { g.Key, Total = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Total, cancellationToken);

    public void Agregar(ConversacionCorreo conversacion) => dbContext.ConversacionesCorreo.Add(conversacion);
}
