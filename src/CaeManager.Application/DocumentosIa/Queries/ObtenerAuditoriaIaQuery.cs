using CaeManager.Application.Common;
using CaeManager.Application.DocumentosIa;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.DocumentosIa.Queries;

public record ObtenerAuditoriaIaQuery(
    string? ProveedorCodigo,
    int Pagina = 1,
    int TamanoPagina = 30) : IRequest<ResultadoPaginado<RegistroAuditoriaIaDto>>;

public record RegistroAuditoriaIaDto(
    Guid Id,
    string HashSha256,
    string TipoEsperado,
    string ProveedorCodigo,
    long TiempoProcesamientoMs,
    decimal? CosteEstimadoOcr,
    decimal? CosteEstimado,
    int NumeroPaginas,
    int ConfianzaGeneral,
    string? Incidencias,
    DateTime CreadaEnUtc);

public class ObtenerAuditoriaIaQueryHandler(IDocumentosIaQueryContext dbContext)
    : IRequestHandler<ObtenerAuditoriaIaQuery, ResultadoPaginado<RegistroAuditoriaIaDto>>
{
    public async Task<ResultadoPaginado<RegistroAuditoriaIaDto>> Handle(ObtenerAuditoriaIaQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.AuditoriasExtraccionIa.AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.ProveedorCodigo))
            consulta = consulta.Where(a => a.ProveedorCodigo == request.ProveedorCodigo);

        var total = await consulta.CountAsync(cancellationToken);

        var elementos = await consulta
            .OrderByDescending(a => a.CreadaEnUtc)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(a => new RegistroAuditoriaIaDto(
                a.Id, a.HashSha256, a.TipoEsperado, a.ProveedorCodigo,
                a.TiempoProcesamientoMs, a.CosteEstimadoOcr, a.CosteEstimado,
                a.NumeroPaginas, a.ConfianzaGeneral, a.Incidencias, a.CreadaEnUtc))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<RegistroAuditoriaIaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
