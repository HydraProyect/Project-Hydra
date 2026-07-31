using CaeManager.Domain.Retencion;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class SolicitudPurgaRepository(CaeManagerDbContext dbContext) : ISolicitudPurgaRepository
{
    /// <summary>
    /// Una solicitud sigue "abierta" mientras no se haya ejecutado ni
    /// cancelado: en cualquiera de esos tres estados hay una propuesta viva
    /// sobre la mesa y no procede levantar otra igual.
    /// </summary>
    private static readonly EstadoSolicitudPurga[] EstadosAbiertos =
    [
        EstadoSolicitudPurga.PendienteDeRevision,
        EstadoSolicitudPurga.TenantAvisado,
        EstadoSolicitudPurga.Programada
    ];

    public Task<SolicitudPurga?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.SolicitudesPurga.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<bool> ExisteAbiertaAsync(TipoDatoPurgable tipoDato, CancellationToken cancellationToken = default) =>
        dbContext.SolicitudesPurga.AnyAsync(
            s => s.TipoDato == tipoDato && EstadosAbiertos.Contains(s.Estado), cancellationToken);

    public async Task<IReadOnlyList<SolicitudPurga>> ObtenerPendientesAsync(CancellationToken cancellationToken = default) =>
        await dbContext.SolicitudesPurga
            .Where(s => EstadosAbiertos.Contains(s.Estado))
            .OrderBy(s => s.DetectadaEnUtc)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SolicitudPurga>> ObtenerEjecutablesAsync(
        DateOnly hoy, CancellationToken cancellationToken = default) =>
        await dbContext.SolicitudesPurga
            .Where(s => s.Estado == EstadoSolicitudPurga.Programada
                        && s.FechaEjecucionProgramada != null
                        && s.FechaEjecucionProgramada <= hoy)
            .ToListAsync(cancellationToken);

    public void Agregar(SolicitudPurga solicitud) => dbContext.SolicitudesPurga.Add(solicitud);
}
