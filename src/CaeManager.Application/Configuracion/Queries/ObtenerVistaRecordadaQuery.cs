using CaeManager.Application.Common;
using CaeManager.Domain.Configuracion;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Configuracion.Queries;

/// <summary>
/// La vista recordada del Usuario actual en una pantalla, en el Tenant actual, tal
/// como la escribió la página (<c>ValoresJson</c> opaco), o <c>null</c> si no hay
/// ninguna. Nunca la de otro Usuario ni la que este mismo Usuario dejó en otro
/// Tenant: el Usuario sale de <see cref="ICurrentUserService"/> y el Tenant lo
/// acotan el filtro global de EF y la política RLS. Sin usuario identificado o sin
/// Tenant activo no hay vista que devolver.
/// </summary>
public record ObtenerVistaRecordadaQuery(string Pantalla) : IRequest<string?>;

public class ObtenerVistaRecordadaQueryHandler(
    IConfiguracionQueryContext dbContext, ICurrentUserService currentUserService, ITenantActual tenantActual)
    : IRequestHandler<ObtenerVistaRecordadaQuery, string?>
{
    public async Task<string?> Handle(ObtenerVistaRecordadaQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return null;
        if (tenantActual.TenantId is not { } tenantId || tenantId == Guid.Empty) return null;

        return await dbContext.FiltrosGuardados
            .Where(f => f.UsuarioId == usuarioId.Value
                && f.Pantalla == request.Pantalla
                && f.Nombre == FiltroGuardado.NombreVistaRecordada)
            .Select(f => f.ValoresJson)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
