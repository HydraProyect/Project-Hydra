using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ConversacionRepository(CaeManagerDbContext dbContext) : IConversacionRepository
{
    public Task<Conversacion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Conversaciones
            .Include(c => c.Mensajes).ThenInclude(m => m.Adjuntos)
            .Include(c => c.Participantes)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<Conversacion?> ObtenerPorHiloExternoAsync(
        Guid conexionIntegracionId, string hiloExternoId, CancellationToken cancellationToken = default) =>
        dbContext.Conversaciones
            .Include(c => c.Mensajes).ThenInclude(m => m.Adjuntos)
            .Include(c => c.Participantes)
            .FirstOrDefaultAsync(c => c.ConexionIntegracionId == conexionIntegracionId && c.HiloExternoId == hiloExternoId, cancellationToken);

    public Task<bool> ExisteMensajeExternoAsync(string mensajeExternoId, CancellationToken cancellationToken = default) =>
        dbContext.Mensajes.AnyAsync(m => m.MensajeExternoId == mensajeExternoId, cancellationToken);

    public Task<Conversacion?> ObtenerAbiertaPorTelefonoAsync(
        Guid conexionIntegracionId, string telefonoContacto, CancellationToken cancellationToken = default) =>
        dbContext.Conversaciones
            .Include(c => c.Mensajes).ThenInclude(m => m.Adjuntos)
            .Include(c => c.Participantes)
            .Where(c => c.Canal == CanalConversacion.WhatsApp
                        && c.ConexionIntegracionId == conexionIntegracionId
                        && c.TelefonoContacto == telefonoContacto
                        && c.Estado != EstadoConversacion.Cerrada)
            .OrderByDescending(c => c.FechaUltimoMensajeUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<Mensaje?> ObtenerMensajePorExternoIdAsync(string mensajeExternoId, CancellationToken cancellationToken = default) =>
        dbContext.Mensajes.FirstOrDefaultAsync(m => m.MensajeExternoId == mensajeExternoId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, int>> ContarWhatsAppVivasPorEjecutivoAsync(
        IReadOnlyCollection<Guid> ejecutivoIds, CancellationToken cancellationToken = default) =>
        await dbContext.Conversaciones
            .Where(c => c.Canal == CanalConversacion.WhatsApp
                        && (c.Estado == EstadoConversacion.Abierta || c.Estado == EstadoConversacion.Pendiente)
                        && c.EjecutivoAsignadoId != null && ejecutivoIds.Contains(c.EjecutivoAsignadoId.Value))
            .GroupBy(c => c.EjecutivoAsignadoId!.Value)
            .Select(g => new { g.Key, Total = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Total, cancellationToken);

    public async Task<IReadOnlyList<Conversacion>> ObtenerAbiertasPorClienteAsync(
        Guid clienteId, Guid excluirConversacionId, CancellationToken cancellationToken = default) =>
        await dbContext.Conversaciones
            .Where(c => c.ClienteId == clienteId && c.Id != excluirConversacionId
                        && (c.Estado == EstadoConversacion.Abierta || c.Estado == EstadoConversacion.Pendiente))
            .OrderByDescending(c => c.FechaUltimoMensajeUtc)
            .ToListAsync(cancellationToken);

    public void Agregar(Conversacion conversacion) => dbContext.Conversaciones.Add(conversacion);
}
