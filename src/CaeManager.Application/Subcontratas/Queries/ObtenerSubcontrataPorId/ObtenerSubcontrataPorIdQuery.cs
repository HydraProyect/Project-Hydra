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

/// <summary>
/// <b>F4.2b — deliberadamente NO migrado a <c>RelacionEmpresarial</c>, igual
/// que <c>ObtenerEmpresaPorIdQuery</c>.</b> Se llegó a migrar y se revirtió al
/// detectarlo una revisión adversarial (2026-08-27), porque este DTO no es de
/// solo lectura: <c>SubcontrataWorkspacePanel</c> carga <c>ClienteIds</c>/
/// <c>EmpresaIds</c> en los selectores y los devuelve tal cual a
/// <c>EditarSubcontrataCommand</c>, que borra todo vínculo presente en
/// "actuales" (repositorio legacy, sin filtro de soft delete) y ausente en
/// "deseados". Un JOIN contra <c>Empresas</c> arrastra su filtro global de
/// soft delete, así que una contraparte eliminada desaparecería de la lectura
/// y el diff la borraría físicamente <b>y</b> cerraría su arista — de forma
/// irreversible, en un flujo que sí ofrece "Deshacer al eliminar", y sin que
/// el usuario haya tocado ese selector.
///
/// Los dos lados del diff tienen que leer la misma fuente. Este lector migra
/// en el mismo incremento que mueva el diff de <c>EditarSubcontrataCommand</c>
/// a <c>RelacionesEmpresariales</c>, no antes. Lo mismo aplica a
/// <c>EjecutarImportacionCombinadaCommand</c>, que lee legacy por el mismo
/// motivo.
/// </summary>
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
