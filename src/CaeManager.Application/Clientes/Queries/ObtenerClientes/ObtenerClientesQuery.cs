using CaeManager.Application.Common;
using CaeManager.Application.Clientes;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerClientes;

public record ObtenerClientesQuery(string? Busqueda, bool? SoloCriticos, int Pagina = 1, int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<ClienteListaDto>>;

public record ClienteListaDto(Guid Id, string RazonSocial, string Cif, bool EsCritico, DateTime CreadoEnUtc);

public class ObtenerClientesQueryHandler(IClientesQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerClientesQuery, ResultadoPaginado<ClienteListaDto>>
{
    public async Task<ResultadoPaginado<ClienteListaDto>> Handle(ObtenerClientesQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.Clientes.AsQueryable();

        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clienteIdsVisibles is not null)
            consulta = consulta.Where(c => clienteIdsVisibles.Contains(c.Id));

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(c => c.RazonSocial.ToUpper().Contains(busqueda));
        }

        if (request.SoloCriticos == true)
            consulta = consulta.Where(c => c.EsCritico);

        var total = await consulta.CountAsync(cancellationToken);

        var elementos = await consulta
            .OrderBy(c => c.RazonSocial)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(c => new ClienteListaDto(c.Id, c.RazonSocial, c.Cif, c.EsCritico, c.CreadoEnUtc))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<ClienteListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
