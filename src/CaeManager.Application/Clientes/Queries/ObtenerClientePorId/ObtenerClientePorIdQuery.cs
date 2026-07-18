using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerClientePorId;

public record ObtenerClientePorIdQuery(Guid Id) : IRequest<ClienteDetalleDto?>;

public record ClienteDetalleDto(Guid Id, string RazonSocial, string Cif, bool EsCritico, string? Notas, DateTime CreadoEnUtc);

public class ObtenerClientePorIdQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerClientePorIdQuery, ClienteDetalleDto?>
{
    public async Task<ClienteDetalleDto?> Handle(ObtenerClientePorIdQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.Id, cancellationToken)) return null;

        return await dbContext.Clientes
            .Where(c => c.Id == request.Id)
            .Select(c => new ClienteDetalleDto(c.Id, c.RazonSocial, c.Cif, c.EsCritico, c.Notas, c.CreadoEnUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
