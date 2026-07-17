using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;

public record ObtenerSubcontratasQuery(string? Busqueda, int Pagina = 1, int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<SubcontrataListaDto>>;

public record SubcontrataListaDto(Guid Id, string RazonSocial, DateTime CreadoEnUtc);

public class ObtenerSubcontratasQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerSubcontratasQuery, ResultadoPaginado<SubcontrataListaDto>>
{
    public async Task<ResultadoPaginado<SubcontrataListaDto>> Handle(ObtenerSubcontratasQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.Subcontratas.AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(s => s.RazonSocial.ToUpper().Contains(busqueda));
        }

        var total = await consulta.CountAsync(cancellationToken);

        var elementos = await consulta
            .OrderBy(s => s.RazonSocial)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(s => new SubcontrataListaDto(s.Id, s.RazonSocial, s.CreadoEnUtc))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<SubcontrataListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
