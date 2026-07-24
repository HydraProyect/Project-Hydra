using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Auditoria.Queries;

/// <summary>
/// Historial completo de auditoría, hoy solo visible embebido en el detalle
/// de cada entidad (ver ROADMAP.md, Fase 4). No resuelve el nombre del
/// usuario aquí — Application no conoce Identity/ApplicationUser (vive en
/// Infrastructure); Web resuelve UsuarioId → email/nombre después, con
/// UserManager, igual que ya hace con el resto de pantallas de Identity.
/// </summary>
public record ObtenerAuditoriaQuery(
    string? EntidadTipo,
    Guid? UsuarioId,
    int Pagina = 1,
    int TamanoPagina = 30,
    Guid? EntidadId = null) : IRequest<ResultadoPaginado<RegistroAuditoriaDto>>;

public record RegistroAuditoriaDto(
    Guid Id,
    string EntidadTipo,
    Guid EntidadId,
    string Accion,
    Guid? UsuarioId,
    DateTime FechaUtc,
    string? DatosAntes,
    string? DatosDespues);

public class ObtenerAuditoriaQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerAuditoriaQuery, ResultadoPaginado<RegistroAuditoriaDto>>
{
    public async Task<ResultadoPaginado<RegistroAuditoriaDto>> Handle(ObtenerAuditoriaQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.RegistrosAuditoria.AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.EntidadTipo))
            consulta = consulta.Where(r => r.EntidadTipo == request.EntidadTipo);

        if (request.UsuarioId is not null)
            consulta = consulta.Where(r => r.UsuarioId == request.UsuarioId);

        if (request.EntidadId is not null)
            consulta = consulta.Where(r => r.EntidadId == request.EntidadId);

        var total = await consulta.CountAsync(cancellationToken);

        var elementos = await consulta
            .OrderByDescending(r => r.FechaUtc)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(r => new RegistroAuditoriaDto(
                r.Id, r.EntidadTipo, r.EntidadId, r.Accion, r.UsuarioId, r.FechaUtc, r.DatosAntes, r.DatosDespues))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<RegistroAuditoriaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
