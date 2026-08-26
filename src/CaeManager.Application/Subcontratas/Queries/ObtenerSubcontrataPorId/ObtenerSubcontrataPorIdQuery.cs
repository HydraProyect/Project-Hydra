using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Subcontratas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerSubcontrataPorId;

public record ObtenerSubcontrataPorIdQuery(Guid Id) : IRequest<SubcontrataDetalleDto?>;

public record SubcontrataDetalleDto(
    Guid Id, string RazonSocial, string? Cif, DateTime CreadoEnUtc, IReadOnlyList<Guid> ClienteIds, IReadOnlyList<Guid> EmpresaIds,
    Guid Version, NivelServicioSubcontrata NivelServicio);

/// <summary>
/// F4.2b (2026-08-27): repuntado de <c>SubcontratasClientes</c>/
/// <c>SubcontratasEmpresas</c> a <c>RelacionesEmpresariales</c> — a
/// diferencia de las tablas legacy (una tabla física por tipo de
/// contraparte), la arista unificada mezcla en el mismo <c>ClienteId</c> lo
/// que puede ser un Cliente real o una Empresa propia (shape
/// Subcontrata→Empresa). El JOIN discriminador aquí no es defensa en
/// profundidad: sin él, cualquier Subcontrata con ambos tipos de relación a
/// la vez (el caso normal, no un caso límite) mezclaría <c>ClienteIds</c> y
/// <c>EmpresaIds</c> en cada consulta — verificado por revisión adversarial
/// independiente antes de implementar, ver convergencia pre-cliente
/// 2026-08-27.
/// </summary>
public class ObtenerSubcontrataPorIdQueryHandler(
    IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos)
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

        var relacionesVigentes = empresasContext.RelacionesEmpresariales
            .Where(r => r.ProveedoraId == request.Id && r.VigenciaHasta == null);

        var clienteIds = await relacionesVigentes
            .Join(empresasContext.Empresas.Where(e => e.EsCritico != null), r => r.ClienteId, e => e.Id, (r, e) => e.Id)
            .ToListAsync(cancellationToken);

        var empresaIds = await relacionesVigentes
            .Join(empresasContext.Empresas.Where(e => e.EsPropia), r => r.ClienteId, e => e.Id, (r, e) => e.Id)
            .ToListAsync(cancellationToken);

        return new SubcontrataDetalleDto(
            subcontrata.Id, subcontrata.RazonSocial, subcontrata.Cif, subcontrata.CreadoEnUtc, clienteIds, empresaIds,
            subcontrata.Version, Enum.Parse<NivelServicioSubcontrata>(subcontrata.NivelServicio));
    }
}
