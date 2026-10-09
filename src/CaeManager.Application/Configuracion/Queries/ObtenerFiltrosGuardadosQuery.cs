using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Configuracion.Queries;

public record ObtenerFiltrosGuardadosQuery(string Pantalla) : IRequest<IReadOnlyList<FiltroGuardadoDto>>;

public record FiltroGuardadoDto(Guid Id, string Nombre, string ValoresJson, DateTime CreadoEnUtc);

/// <summary>
/// Filtros guardados del usuario actual en una pantalla, <b>solo los del Tenant
/// activo de la sesión</b>. La tabla no lleva filtro global de Tenant ni RLS
/// (ver <c>FiltroGuardadoConfiguration</c>): la frontera es la clave compuesta
/// de <see cref="PantallasConFiltrosGuardados.ClaveAlmacenada"/>, igualada por
/// completo — nunca por prefijo, que casaría las filas antiguas sin Tenant y
/// las de los demás Tenant.
/// </summary>
public class ObtenerFiltrosGuardadosQueryHandler(
    IConfiguracionQueryContext dbContext, ICurrentUserService currentUserService, ITenantActual tenantActual)
    : IRequestHandler<ObtenerFiltrosGuardadosQuery, IReadOnlyList<FiltroGuardadoDto>>
{
    public async Task<IReadOnlyList<FiltroGuardadoDto>> Handle(ObtenerFiltrosGuardadosQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return [];

        var clave = PantallasConFiltrosGuardados.ClaveAlmacenada(request.Pantalla, tenantActual.TenantId);
        if (clave is null) return [];

        return await dbContext.FiltrosGuardados
            .Where(f => f.UsuarioId == usuarioId.Value && f.Pantalla == clave)
            .OrderBy(f => f.Nombre)
            .Select(f => new FiltroGuardadoDto(f.Id, f.Nombre, f.ValoresJson, f.CreadoEnUtc))
            .ToListAsync(cancellationToken);
    }
}
