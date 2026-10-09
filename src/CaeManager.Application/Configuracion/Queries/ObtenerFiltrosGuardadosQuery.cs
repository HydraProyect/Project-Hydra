using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Configuracion.Queries;

public record ObtenerFiltrosGuardadosQuery(string Pantalla) : IRequest<IReadOnlyList<FiltroGuardadoDto>>;

public record FiltroGuardadoDto(Guid Id, string Nombre, string ValoresJson, DateTime CreadoEnUtc);

public class ObtenerFiltrosGuardadosQueryHandler(IConfiguracionQueryContext dbContext, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerFiltrosGuardadosQuery, IReadOnlyList<FiltroGuardadoDto>>
{
    public async Task<IReadOnlyList<FiltroGuardadoDto>> Handle(ObtenerFiltrosGuardadosQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return [];

        return await dbContext.FiltrosGuardados
            .Where(f => f.UsuarioId == usuarioId.Value && f.Pantalla == request.Pantalla)
            .OrderBy(f => f.Nombre)
            .Select(f => new FiltroGuardadoDto(f.Id, f.Nombre, f.ValoresJson, f.CreadoEnUtc))
            .ToListAsync(cancellationToken);
    }
}
