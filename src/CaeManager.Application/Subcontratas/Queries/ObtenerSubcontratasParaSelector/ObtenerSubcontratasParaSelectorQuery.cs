using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;

/// <summary>
/// Lista ligera para poblar selectores. Con EmpresaId, se restringe a las
/// Subcontratas que ya prestan servicio a esa Empresa — sin EmpresaId,
/// devuelve todas.
/// </summary>
public record ObtenerSubcontratasParaSelectorQuery(Guid? EmpresaId = null) : IRequest<IReadOnlyList<SubcontrataSelectorDto>>;

public record SubcontrataSelectorDto(Guid Id, string RazonSocial);

public class ObtenerSubcontratasParaSelectorQueryHandler(ISubcontratasQueryContext dbContext)
    : IRequestHandler<ObtenerSubcontratasParaSelectorQuery, IReadOnlyList<SubcontrataSelectorDto>>
{
    public async Task<IReadOnlyList<SubcontrataSelectorDto>> Handle(
        ObtenerSubcontratasParaSelectorQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.Subcontratas.AsQueryable();

        if (request.EmpresaId is not null)
        {
            var subcontrataIdsAsociadas = dbContext.SubcontratasEmpresas
                .Where(se => se.EmpresaId == request.EmpresaId)
                .Select(se => se.SubcontrataId);

            consulta = consulta.Where(s => subcontrataIdsAsociadas.Contains(s.Id));
        }

        return await consulta
            .OrderBy(s => s.RazonSocial)
            .Select(s => new SubcontrataSelectorDto(s.Id, s.RazonSocial))
            .ToListAsync(cancellationToken);
    }
}
