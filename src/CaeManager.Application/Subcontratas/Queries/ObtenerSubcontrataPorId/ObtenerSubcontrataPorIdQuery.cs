using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerSubcontrataPorId;

public record ObtenerSubcontrataPorIdQuery(Guid Id) : IRequest<SubcontrataDetalleDto?>;

public record SubcontrataDetalleDto(
    Guid Id, string RazonSocial, DateTime CreadoEnUtc, IReadOnlyList<Guid> ClienteIds, IReadOnlyList<Guid> EmpresaIds);

public class ObtenerSubcontrataPorIdQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerSubcontrataPorIdQuery, SubcontrataDetalleDto?>
{
    public async Task<SubcontrataDetalleDto?> Handle(ObtenerSubcontrataPorIdQuery request, CancellationToken cancellationToken)
    {
        var subcontrata = await dbContext.Subcontratas
            .Where(s => s.Id == request.Id)
            .Select(s => new { s.Id, s.RazonSocial, s.CreadoEnUtc })
            .FirstOrDefaultAsync(cancellationToken);

        if (subcontrata is null) return null;

        var clienteIds = await dbContext.SubcontratasClientes
            .Where(sc => sc.SubcontrataId == request.Id)
            .Select(sc => sc.ClienteId)
            .ToListAsync(cancellationToken);

        var empresaIds = await dbContext.SubcontratasEmpresas
            .Where(se => se.SubcontrataId == request.Id)
            .Select(se => se.EmpresaId)
            .ToListAsync(cancellationToken);

        return new SubcontrataDetalleDto(subcontrata.Id, subcontrata.RazonSocial, subcontrata.CreadoEnUtc, clienteIds, empresaIds);
    }
}
