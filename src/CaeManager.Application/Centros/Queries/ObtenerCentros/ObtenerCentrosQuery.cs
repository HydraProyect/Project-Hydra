using CaeManager.Application.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Centros;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerCentros;

public record ObtenerCentrosQuery(string? Busqueda, Guid? ClienteId, int Pagina = 1, int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<CentroListaDto>>;

public record CentroListaDto(
    Guid Id, string Nombre, string? CodigoCentro, Guid ClienteId, string ClienteRazonSocial, string EmpresaRazonSocial,
    EstadoCentro Estado);

public class ObtenerCentrosQueryHandler(
    ICentrosQueryContext centrosContext, IClientesQueryContext clientesContext, IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos, ICalculoEstadoCentroService calculoEstadoCentro)
    : IRequestHandler<ObtenerCentrosQuery, ResultadoPaginado<CentroListaDto>>
{
    public async Task<ResultadoPaginado<CentroListaDto>> Handle(ObtenerCentrosQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from centro in centrosContext.Centros
            join cliente in clientesContext.Clientes on centro.ClienteId equals cliente.Id
            join empresa in empresasContext.Empresas on centro.EmpresaId equals empresa.Id
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

        var pagina = await consulta
            .OrderBy(x => x.cliente.RazonSocial).ThenBy(x => x.centro.Nombre)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new
            {
                x.centro.Id,
                x.centro.Nombre,
                x.centro.CodigoCentro,
                x.centro.ClienteId,
                ClienteRazonSocial = x.cliente.RazonSocial,
                EmpresaRazonSocial = x.empresa.RazonSocial
            })
            .ToListAsync(cancellationToken);

        var estados = await calculoEstadoCentro.CalcularAsync(pagina.Select(c => c.Id).ToList(), cancellationToken);

        var elementos = pagina
            .Select(c => new CentroListaDto(
                c.Id, c.Nombre, c.CodigoCentro, c.ClienteId, c.ClienteRazonSocial, c.EmpresaRazonSocial,
                estados.TryGetValue(c.Id, out var resultado) ? resultado.Estado : EstadoCentro.Vigente))
            .ToList();

        return new ResultadoPaginado<CentroListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
