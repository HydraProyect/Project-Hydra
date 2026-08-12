using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;

public record ObtenerCentrosParaSelectorQuery(Guid? ClienteId = null, Guid? EmpresaId = null)
    : IRequest<IReadOnlyList<CentroSelectorDto>>;

public record CentroSelectorDto(Guid Id, string Nombre, string ClienteRazonSocial, string EmpresaRazonSocial);

public class ObtenerCentrosParaSelectorQueryHandler(ICentrosQueryContext centrosContext, IClientesQueryContext clientesContext, IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCentrosParaSelectorQuery, IReadOnlyList<CentroSelectorDto>>
{
    public async Task<IReadOnlyList<CentroSelectorDto>> Handle(
        ObtenerCentrosParaSelectorQuery request, CancellationToken cancellationToken)
    {
        var consulta = from centro in centrosContext.Centros
                       join cliente in clientesContext.Clientes on centro.ClienteId equals cliente.Id
                       join empresa in empresasContext.Empresas on centro.EmpresaId equals empresa.Id
                       select new { centro, cliente, empresa };

        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centroIdsVisibles is not null)
            consulta = consulta.Where(x => centroIdsVisibles.Contains(x.centro.Id));

        if (request.ClienteId is not null)
            consulta = consulta.Where(x => x.centro.ClienteId == request.ClienteId);

        if (request.EmpresaId is not null)
            consulta = consulta.Where(x => x.centro.EmpresaId == request.EmpresaId);

        return await consulta
            .OrderBy(x => x.cliente.RazonSocial).ThenBy(x => x.centro.Nombre)
            .Select(x => new CentroSelectorDto(x.centro.Id, x.centro.Nombre, x.cliente.RazonSocial, x.empresa.RazonSocial))
            .ToListAsync(cancellationToken);
    }
}
