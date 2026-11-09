using System.Security.Cryptography;
using System.Text;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ConversacionRepository(CaeManagerDbContext dbContext) : IConversacionRepository
{
    public Task<Conversacion?> ObtenerUltimaCerradaPorHiloExternoAsync(
        Guid conexionIntegracionId, string hiloExternoId, CancellationToken cancellationToken = default) =>
        dbContext.Conversaciones
            .Where(c => c.ConexionIntegracionId == conexionIntegracionId && c.HiloExternoId == hiloExternoId
                        && c.Estado == EstadoConversacion.Cerrada)
            .OrderByDescending(c => c.EstadoDesdeUtc)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);

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
            .FirstOrDefaultAsync(c => c.ConexionIntegracionId == conexionIntegracionId && c.HiloExternoId == hiloExternoId
                                     && c.Estado != EstadoConversacion.Cerrada, cancellationToken);

    public Task<bool> ExisteMensajeExternoAsync(Guid conexionIntegracionId, string mensajeExternoId, CancellationToken cancellationToken = default) =>
        (from m in dbContext.Mensajes
         join c in dbContext.Conversaciones on m.ConversacionId equals c.Id
         where c.ConexionIntegracionId == conexionIntegracionId && m.MensajeExternoId == mensajeExternoId
         select m).AnyAsync(cancellationToken);

    public Task<DateTime?> ObtenerFechaUltimoMensajeEntranteAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default) =>
        (from m in dbContext.Mensajes
         join c in dbContext.Conversaciones on m.ConversacionId equals c.Id
         where c.ConexionIntegracionId == conexionIntegracionId && m.Direccion == DireccionMensaje.Entrante
         select (DateTime?)m.FechaUtc).MaxAsync(cancellationToken);

    public async Task<bool> ReservarAcuseAsync(
        Guid tenantId, Guid conexionIntegracionId, string remitente, DateTime ahoraUtc, TimeSpan intervalo,
        CancellationToken cancellationToken = default)
    {
        var huella = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(remitente.Trim().ToLowerInvariant())));
        var vigenteDesdeUtc = ahoraUtc - intervalo;

        // ON CONFLICT … DO UPDATE … WHERE bloquea la fila y evalúa la condición sobre su última versión confirmada: de
        // dos sentencias simultáneas, la segunda no actualiza nada (0 filas). Va en su propia sentencia, sin SaveChanges,
        // para quedar confirmada antes de enviar. La política de RLS comprueba el Tenant de la fila.
        return await dbContext.Database.ExecuteSqlAsync($"""
            INSERT INTO "ReservasAcuseBuzon" ("TenantId", "ConexionIntegracionId", "HuellaRemitente", "ReservadaEnUtc")
            VALUES ({tenantId}, {conexionIntegracionId}, {huella}, {ahoraUtc})
            ON CONFLICT ("TenantId", "ConexionIntegracionId", "HuellaRemitente")
            DO UPDATE SET "ReservadaEnUtc" = EXCLUDED."ReservadaEnUtc"
            WHERE "ReservasAcuseBuzon"."ReservadaEnUtc" < {vigenteDesdeUtc}
            """, cancellationToken) == 1;
    }

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

    public Task<Mensaje?> ObtenerMensajePorExternoIdAsync(Guid conexionIntegracionId, string mensajeExternoId, CancellationToken cancellationToken = default) =>
        (from m in dbContext.Mensajes
         join c in dbContext.Conversaciones on m.ConversacionId equals c.Id
         where c.ConexionIntegracionId == conexionIntegracionId && m.MensajeExternoId == mensajeExternoId
         select m).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, int>> ContarWhatsAppVivasPorEjecutivoAsync(
        IReadOnlyCollection<Guid> ejecutivoIds, CancellationToken cancellationToken = default) =>
        await dbContext.Conversaciones
            .Where(c => c.Canal == CanalConversacion.WhatsApp
                        && TransicionesEstadoConversacion.EstadosVivos.Contains(c.Estado)
                        && c.EjecutivoAsignadoId != null && ejecutivoIds.Contains(c.EjecutivoAsignadoId.Value))
            .GroupBy(c => c.EjecutivoAsignadoId!.Value)
            .Select(g => new { g.Key, Total = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Total, cancellationToken);

    public async Task<IReadOnlyList<Conversacion>> ObtenerAbiertasPorClienteAsync(
        Guid clienteId, Guid excluirConversacionId, CancellationToken cancellationToken = default) =>
        await dbContext.Conversaciones
            .Where(c => c.ClienteId == clienteId && c.Id != excluirConversacionId
                        && TransicionesEstadoConversacion.EstadosVivos.Contains(c.Estado))
            .OrderByDescending(c => c.FechaUltimoMensajeUtc)
            .ToListAsync(cancellationToken);

    public void Agregar(Conversacion conversacion) => dbContext.Conversaciones.Add(conversacion);
}
