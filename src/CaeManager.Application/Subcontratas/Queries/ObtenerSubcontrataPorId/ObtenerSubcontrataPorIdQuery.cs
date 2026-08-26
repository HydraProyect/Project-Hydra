using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Subcontratas;
using CaeManager.Domain.Subcontratas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerSubcontrataPorId;

public record ObtenerSubcontrataPorIdQuery(Guid Id) : IRequest<SubcontrataDetalleDto?>;

public record SubcontrataDetalleDto(
    Guid Id, string RazonSocial, string? Cif, DateTime CreadoEnUtc, IReadOnlyList<Guid> ClienteIds, IReadOnlyList<Guid> EmpresaIds,
    Guid Version, NivelServicioSubcontrata NivelServicio);

public class ObtenerSubcontrataPorIdQueryHandler(
    IEmpresasQueryContext empresasContext, ISubcontratasQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerSubcontrataPorIdQuery, SubcontrataDetalleDto?>
{
    public async Task<SubcontrataDetalleDto?> Handle(ObtenerSubcontrataPorIdQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.SubcontrataVisibleAsync(request.Id, cancellationToken)) return null;

        var subcontrata = await empresasContext.Empresas
            .Where(e => e.Id == request.Id)
            .Select(e => new { e.Id, e.RazonSocial, e.Cif, e.CreadoEnUtc, e.Version, e.NivelServicio })
            .FirstOrDefaultAsync(cancellationToken);

        if (subcontrata is null || subcontrata.NivelServicio is null) return null;

        var clienteIds = await dbContext.SubcontratasClientes
            .Where(sc => sc.SubcontrataId == request.Id)
            .Select(sc => sc.ClienteId)
            .ToListAsync(cancellationToken);

        var empresaIds = await dbContext.SubcontratasEmpresas
            .Where(se => se.SubcontrataId == request.Id)
            .Select(se => se.EmpresaId)
            .ToListAsync(cancellationToken);

        return new SubcontrataDetalleDto(
            subcontrata.Id, subcontrata.RazonSocial, subcontrata.Cif, subcontrata.CreadoEnUtc, clienteIds, empresaIds,
            subcontrata.Version, Enum.Parse<NivelServicioSubcontrata>(subcontrata.NivelServicio));
    }
}
