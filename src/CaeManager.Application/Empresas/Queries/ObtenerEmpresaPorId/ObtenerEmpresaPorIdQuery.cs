using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;

public record ObtenerEmpresaPorIdQuery(Guid Id) : IRequest<EmpresaDetalleDto?>;

public record EmpresaDetalleDto(Guid Id, string RazonSocial, string? Cif, DateTime CreadoEnUtc, IReadOnlyList<Guid> ClienteIds);

public class ObtenerEmpresaPorIdQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerEmpresaPorIdQuery, EmpresaDetalleDto?>
{
    public async Task<EmpresaDetalleDto?> Handle(ObtenerEmpresaPorIdQuery request, CancellationToken cancellationToken)
    {
        var empresa = await dbContext.Empresas
            .Where(e => e.Id == request.Id)
            .Select(e => new { e.Id, e.RazonSocial, e.Cif, e.CreadoEnUtc })
            .FirstOrDefaultAsync(cancellationToken);

        if (empresa is null) return null;

        var clienteIds = await dbContext.EmpresasClientes
            .Where(ec => ec.EmpresaId == request.Id)
            .Select(ec => ec.ClienteId)
            .ToListAsync(cancellationToken);

        return new EmpresaDetalleDto(empresa.Id, empresa.RazonSocial, empresa.Cif, empresa.CreadoEnUtc, clienteIds);
    }
}
