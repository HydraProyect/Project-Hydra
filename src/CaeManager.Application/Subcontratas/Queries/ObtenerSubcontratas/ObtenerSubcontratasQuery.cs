using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;

public record ObtenerSubcontratasQuery(string? Busqueda, int Pagina = 1, int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<SubcontrataListaDto>>;

public record SubcontrataListaDto(Guid Id, string RazonSocial, DateTime CreadoEnUtc);

public class ObtenerSubcontratasQueryHandler(ISubcontratasQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerSubcontratasQuery, ResultadoPaginado<SubcontrataListaDto>>
{
    public async Task<ResultadoPaginado<SubcontrataListaDto>> Handle(ObtenerSubcontratasQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.Subcontratas.AsQueryable();

        var subcontrataIdsVisibles = await alcanceDatos.ObtenerSubcontrataIdsVisiblesAsync(cancellationToken);
        if (subcontrataIdsVisibles is not null)
            consulta = consulta.Where(s => subcontrataIdsVisibles.Contains(s.Id));

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
