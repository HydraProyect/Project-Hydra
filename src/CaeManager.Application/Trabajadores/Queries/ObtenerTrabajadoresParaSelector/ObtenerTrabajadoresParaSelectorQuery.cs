using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;

public record ObtenerTrabajadoresParaSelectorQuery : IRequest<IReadOnlyList<TrabajadorSelectorDto>>;

public record TrabajadorSelectorDto(Guid Id, string NombreCompleto, string? Dni, string? Alias);

public class ObtenerTrabajadoresParaSelectorQueryHandler(ITrabajadoresQueryContext dbContext)
    : IRequestHandler<ObtenerTrabajadoresParaSelectorQuery, IReadOnlyList<TrabajadorSelectorDto>>
{
    public async Task<IReadOnlyList<TrabajadorSelectorDto>> Handle(
        ObtenerTrabajadoresParaSelectorQuery request, CancellationToken cancellationToken) =>
        await dbContext.Trabajadores
            .OrderBy(t => t.Apellidos).ThenBy(t => t.Nombre)
            .Select(t => new TrabajadorSelectorDto(t.Id, t.Nombre + " " + t.Apellidos, t.Dni, t.Alias))
            .ToListAsync(cancellationToken);
}
