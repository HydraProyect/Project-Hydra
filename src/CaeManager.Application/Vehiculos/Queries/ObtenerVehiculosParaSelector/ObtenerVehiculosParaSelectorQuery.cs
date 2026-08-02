using CaeManager.Application.Common;
using CaeManager.Application.Vehiculos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Vehiculos.Queries.ObtenerVehiculosParaSelector;

public record ObtenerVehiculosParaSelectorQuery : IRequest<IReadOnlyList<VehiculoSelectorDto>>;

public record VehiculoSelectorDto(Guid Id, string Nombre, string NumeroPlaca);

public class ObtenerVehiculosParaSelectorQueryHandler(IVehiculosQueryContext dbContext)
    : IRequestHandler<ObtenerVehiculosParaSelectorQuery, IReadOnlyList<VehiculoSelectorDto>>
{
    public async Task<IReadOnlyList<VehiculoSelectorDto>> Handle(
        ObtenerVehiculosParaSelectorQuery request, CancellationToken cancellationToken) =>
        await dbContext.Vehiculos
            .OrderBy(v => v.Nombre)
            .Select(v => new VehiculoSelectorDto(v.Id, v.Nombre, v.NumeroPlaca))
            .ToListAsync(cancellationToken);
}
