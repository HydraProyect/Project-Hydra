using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerEmpresas;

public record ObtenerEmpresasQuery(string? Busqueda, int Pagina = 1, int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<EmpresaListaDto>>;

public record EmpresaListaDto(Guid Id, string RazonSocial, string? Cif, DateTime CreadoEnUtc);

public class ObtenerEmpresasQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerEmpresasQuery, ResultadoPaginado<EmpresaListaDto>>
{
    public async Task<ResultadoPaginado<EmpresaListaDto>> Handle(ObtenerEmpresasQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.Empresas.AsQueryable();

        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        if (empresaIdsVisibles is not null)
            consulta = consulta.Where(e => empresaIdsVisibles.Contains(e.Id));

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(e => e.RazonSocial.ToUpper().Contains(busqueda));
        }

        var total = await consulta.CountAsync(cancellationToken);

        var elementos = await consulta
            .OrderBy(e => e.RazonSocial)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(e => new EmpresaListaDto(e.Id, e.RazonSocial, e.Cif, e.CreadoEnUtc))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<EmpresaListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
