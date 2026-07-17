using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;

/// <summary>Lista completa y ligera para poblar selectores (p. ej. al crear un Centro).</summary>
public record ObtenerClientesParaSelectorQuery : IRequest<IReadOnlyList<ClienteSelectorDto>>;

public record ClienteSelectorDto(Guid Id, string RazonSocial);

public class ObtenerClientesParaSelectorQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerClientesParaSelectorQuery, IReadOnlyList<ClienteSelectorDto>>
{
    public async Task<IReadOnlyList<ClienteSelectorDto>> Handle(
        ObtenerClientesParaSelectorQuery request, CancellationToken cancellationToken) =>
        await dbContext.Clientes
            .OrderBy(c => c.RazonSocial)
            .Select(c => new ClienteSelectorDto(c.Id, c.RazonSocial))
            .ToListAsync(cancellationToken);
}
