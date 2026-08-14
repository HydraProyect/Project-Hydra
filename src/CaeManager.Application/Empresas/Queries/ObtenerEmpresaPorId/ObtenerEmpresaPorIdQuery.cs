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

        var clienteIds = await dbContext.EmpresasClientes
            .Where(ec => ec.EmpresaId == request.Id)
            .Select(ec => ec.ClienteId)
            .ToListAsync(cancellationToken);

        return new EmpresaDetalleDto(
            empresa.Id, empresa.RazonSocial, empresa.Cif, empresa.CreadoEnUtc, clienteIds, empresa.Version,
            empresa.Cnae, empresa.ConvenioAplicable, empresa.EsActividadAnexoI);
    }
}
