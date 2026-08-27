using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;

public record ObtenerEmpresaPorIdQuery(Guid Id) : IRequest<EmpresaDetalleDto?>;

public record EmpresaDetalleDto(
    Guid Id,
    string RazonSocial,
    string? Cif,
    DateTime CreadoEnUtc,
    IReadOnlyList<Guid> ClienteIds,
    Guid Version,
    string? Cnae = null,
    string? ConvenioAplicable = null,
    bool EsActividadAnexoI = false);

/// <summary>
/// F4.2c: <c>ClienteIds</c> sale de la arista, con el MISMO criterio de
/// clasificación que usa el diff de escritura de <c>EditarEmpresaCommand</c>
/// (eje Cliente = <c>EsCritico != null</c>) — los dos lados leen la misma
/// fuente, que es la condición para que este DTO pueda realimentar la
/// edición sin cerrar nada por desalineamiento. Dos invariantes deliberados:
/// una contraparte soft-deleted no aparece aquí NI entra en "actuales" del
/// diff (opaca en ambos lados → la relación sobrevive intacta a una edición
/// que no la menciona); y <c>ClienteIds</c> NO se acota por cartera — un
/// Cliente vivo pero fuera del alcance del usuario debe seguir viajando en
/// el DTO, porque hoy el diff de bajas se define sobre este contenido y
/// acotarlo reabriría el cierre silencioso por la puerta del alcance.
/// </summary>
public class ObtenerEmpresaPorIdQueryHandler(IEmpresasQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerEmpresaPorIdQuery, EmpresaDetalleDto?>
{
    public async Task<EmpresaDetalleDto?> Handle(ObtenerEmpresaPorIdQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.EmpresaVisibleAsync(request.Id, cancellationToken)) return null;

        var empresa = await dbContext.Empresas
            .Where(e => e.Id == request.Id)
            .Select(e => new { e.Id, e.RazonSocial, e.Cif, e.CreadoEnUtc, e.Version, e.Cnae, e.ConvenioAplicable, e.EsActividadAnexoI })
            .FirstOrDefaultAsync(cancellationToken);

        if (empresa is null) return null;

        var clienteIds = await dbContext.RelacionesEmpresariales
            .Where(r => r.ProveedoraId == request.Id && r.VigenciaHasta == null)
            .Join(dbContext.Empresas.Where(e => e.EsCritico != null), r => r.ClienteId, e => e.Id, (r, e) => e.Id)
            .ToListAsync(cancellationToken);

        return new EmpresaDetalleDto(
            empresa.Id, empresa.RazonSocial, empresa.Cif, empresa.CreadoEnUtc, clienteIds, empresa.Version,
            empresa.Cnae, empresa.ConvenioAplicable, empresa.EsActividadAnexoI);
    }
}
