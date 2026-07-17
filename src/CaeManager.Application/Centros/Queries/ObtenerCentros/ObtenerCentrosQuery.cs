using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerCentros;

public record ObtenerCentrosQuery(string? Busqueda, Guid? ClienteId, int Pagina = 1, int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<CentroListaDto>>;

public record CentroListaDto(
    Guid Id, string Nombre, string? CodigoCentro, Guid ClienteId, string ClienteRazonSocial, string EmpresaRazonSocial);

public class ObtenerCentrosQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCentrosQuery, ResultadoPaginado<CentroListaDto>>
{
    public async Task<ResultadoPaginado<CentroListaDto>> Handle(ObtenerCentrosQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from centro in dbContext.Centros
            join cliente in dbContext.Clientes on centro.ClienteId equals cliente.Id
            join empresa in dbContext.Empresas on centro.EmpresaId equals empresa.Id
            select new { centro, cliente, empresa };

        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centroIdsVisibles is not null)
            consulta = consulta.Where(x => centroIdsVisibles.Contains(x.centro.Id));

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(x => x.centro.Nombre.ToUpper().Contains(busqueda));
        }

        if (request.ClienteId is not null)
            consulta = consulta.Where(x => x.centro.ClienteId == request.ClienteId);

        var total = await consulta.CountAsync(cancellationToken);

        var elementos = await consulta
            .OrderBy(x => x.cliente.RazonSocial).ThenBy(x => x.centro.Nombre)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new CentroListaDto(
                x.centro.Id, x.centro.Nombre, x.centro.CodigoCentro, x.centro.ClienteId, x.cliente.RazonSocial, x.empresa.RazonSocial))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<CentroListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
