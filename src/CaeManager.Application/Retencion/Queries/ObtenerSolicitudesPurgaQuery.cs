using CaeManager.Application.Common;
using CaeManager.Domain.Retencion;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Retencion.Queries;

/// <summary>
/// Las propuestas de purga del tenant, las abiertas primero. Incluye las ya
/// resueltas —ejecutadas y canceladas— porque son el registro de qué se
/// destruyó, cuándo y con qué autorización: es lo que se enseña si alguien
/// pregunta.
/// </summary>
public record ObtenerSolicitudesPurgaQuery : IRequest<IReadOnlyList<SolicitudPurgaDto>>;

public record SolicitudPurgaDto(
    Guid Id,
    TipoDatoPurgable TipoDato,
    EstadoSolicitudPurga Estado,
    int RegistrosAfectados,
    DateOnly FechaCorte,
    DateTime DetectadaEnUtc,
    DateTime? AvisadaAlTenantEnUtc,
    DateOnly? FechaEjecucionProgramada,
    DateTime? EjecutadaEnUtc,
    string? Motivo)
{
    public bool EstaResuelta => Estado is EstadoSolicitudPurga.Ejecutada or EstadoSolicitudPurga.Cancelada;

    public bool PuedeEjecutarseHoy =>
        Estado == EstadoSolicitudPurga.Programada &&
        FechaEjecucionProgramada is { } fecha &&
        DateOnly.FromDateTime(DateTime.UtcNow) >= fecha;
}

public class ObtenerSolicitudesPurgaQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerSolicitudesPurgaQuery, IReadOnlyList<SolicitudPurgaDto>>
{
    public async Task<IReadOnlyList<SolicitudPurgaDto>> Handle(
        ObtenerSolicitudesPurgaQuery request, CancellationToken cancellationToken) =>
        await dbContext.SolicitudesPurga
            .OrderBy(s => s.Estado == EstadoSolicitudPurga.Ejecutada || s.Estado == EstadoSolicitudPurga.Cancelada)
            .ThenByDescending(s => s.DetectadaEnUtc)
            .Select(s => new SolicitudPurgaDto(
                s.Id, s.TipoDato, s.Estado, s.RegistrosAfectados, s.FechaCorte, s.DetectadaEnUtc,
                s.AvisadaAlTenantEnUtc, s.FechaEjecucionProgramada, s.EjecutadaEnUtc, s.Motivo))
            .ToListAsync(cancellationToken);
}
