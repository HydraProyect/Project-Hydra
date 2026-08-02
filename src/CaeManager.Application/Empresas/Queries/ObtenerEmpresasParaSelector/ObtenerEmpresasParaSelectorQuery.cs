using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;

/// <summary>
/// Lista ligera para poblar selectores. Con ClienteId, se restringe a las
/// Empresas ya asociadas a ese Cliente (p. ej. al elegir la Empresa de un
/// Centro nuevo) — sin ClienteId, devuelve todas (p. ej. al dar de alta un
/// Trabajador, donde la Empresa no depende de ningún Cliente concreto).
/// </summary>
public record ObtenerEmpresasParaSelectorQuery(Guid? ClienteId = null) : IRequest<IReadOnlyList<EmpresaSelectorDto>>;

public record EmpresaSelectorDto(Guid Id, string RazonSocial);

public class ObtenerEmpresasParaSelectorQueryHandler(IEmpresasQueryContext dbContext)
    : IRequestHandler<ObtenerEmpresasParaSelectorQuery, IReadOnlyList<EmpresaSelectorDto>>
{
    public async Task<IReadOnlyList<EmpresaSelectorDto>> Handle(
        ObtenerEmpresasParaSelectorQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.Empresas.AsQueryable();

        if (request.ClienteId is not null)
        {
            var empresaIdsAsociadas = dbContext.EmpresasClientes
                .Where(ec => ec.ClienteId == request.ClienteId)
                .Select(ec => ec.EmpresaId);

            consulta = consulta.Where(e => empresaIdsAsociadas.Contains(e.Id));
        }

        return await consulta
            .OrderBy(e => e.RazonSocial)
            .Select(e => new EmpresaSelectorDto(e.Id, e.RazonSocial))
            .ToListAsync(cancellationToken);
    }
}
