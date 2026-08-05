using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;

public record ObtenerSubcontratasQuery(
    string? Busqueda, int Pagina = 1, int TamanoPagina = 20,
    string? OrdenarPor = null, bool Descendente = false)
    : IRequest<ResultadoPaginado<SubcontrataListaDto>>;

public record SubcontrataListaDto(Guid Id, string RazonSocial, string? Cif, DateTime CreadoEnUtc);

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
            consulta = consulta.Where(s => s.RazonSocial.ToUpper().Contains(busqueda)
                || (s.Cif != null && s.Cif.ToUpper().Contains(busqueda)));
        }

        var total = await consulta.CountAsync(cancellationToken);

        // Lista blanca de columnas ordenables — ver ObtenerClientesQuery.
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(SubcontrataListaDto.RazonSocial), true) => consulta.OrderByDescending(s => s.RazonSocial),
            (nameof(SubcontrataListaDto.Cif), false) => consulta.OrderBy(s => s.Cif),
            (nameof(SubcontrataListaDto.Cif), true) => consulta.OrderByDescending(s => s.Cif),
            (nameof(SubcontrataListaDto.CreadoEnUtc), false) => consulta.OrderBy(s => s.CreadoEnUtc),
            (nameof(SubcontrataListaDto.CreadoEnUtc), true) => consulta.OrderByDescending(s => s.CreadoEnUtc),
            _ => consulta.OrderBy(s => s.RazonSocial)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(s => s.Id);

        var elementos = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(s => new SubcontrataListaDto(s.Id, s.RazonSocial, s.Cif, s.CreadoEnUtc))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<SubcontrataListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
