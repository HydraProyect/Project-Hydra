using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Application.VistaDemo;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;

/// <summary>
/// Los clientes que el usuario actual puede activar como Delegated Workspace
/// (ADR-004 § 6): siempre su propio tenant de origen, más los Clientes
/// Delegantes de cualquier <c>DelegacionTenant</c> activa donde tenga una
/// <c>AsignacionOperadorDelegado</c> vigente. Para un usuario que no es
/// Operador Delegado de nadie, el resultado tiene un único elemento — el
/// caso de hoy, sin cambios (ver <c>SelectorClienteActivo.razor</c>, que no
/// se muestra cuando esta lista tiene un solo elemento).
/// </summary>
public record ObtenerClientesAutorizadosQuery : IRequest<IReadOnlyList<ClienteAutorizadoDto>>;

public record ClienteAutorizadoDto(Guid TenantId, string Nombre, bool EsOrigen);

public class ObtenerClientesAutorizadosQueryHandler(
    ITenantsQueryContext dbContext, ICurrentUserService currentUserService, IVistaDemoActual? vistaDemo = null)
    : IRequestHandler<ObtenerClientesAutorizadosQuery, IReadOnlyList<ClienteAutorizadoDto>>
{
    public async Task<IReadOnlyList<ClienteAutorizadoDto>> Handle(
        ObtenerClientesAutorizadosQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();

        if (usuarioId is null || tenantOrigenId is null)
            return [];

        var ahora = DateTime.UtcNow;
        var resultado = new List<ClienteAutorizadoDto>();

        var tenantOrigen = await dbContext.Tenants
            .Where(t => t.Id == tenantOrigenId.Value)
            .Select(t => new ClienteAutorizadoDto(t.Id, t.Nombre, true))
            .FirstOrDefaultAsync(cancellationToken);

        if (tenantOrigen is not null)
            resultado.Add(tenantOrigen);

        var delegados = await (
            from asignacion in dbContext.AsignacionesOperadorDelegado
            join delegacion in dbContext.DelegacionesTenant on asignacion.DelegacionTenantId equals delegacion.Id
            join tenant in dbContext.Tenants on delegacion.TenantClienteId equals tenant.Id
            // Activa Y no caducada: una ventana de soporte vencida deja de
            // conceder acceso sin que nadie la toque (ver
            // DelegacionTenant.EstaVigente — aquí va escrita a mano porque el
            // método de dominio no se traduce a SQL).
            where asignacion.UsuarioId == usuarioId.Value && delegacion.Activa
                  && (delegacion.ExpiraEnUtc == null || delegacion.ExpiraEnUtc > ahora)
            select new ClienteAutorizadoDto(tenant.Id, tenant.Nombre, false))
            .ToListAsync(cancellationToken);

        // Distinct por tenant, en memoria: un operador puede tener más de una
        // AsignacionOperadorDelegado activa hacia el mismo Cliente Delegante
        // (p. ej. una Comercial y una de Soporte, como el Administrador de la
        // siembra de demo) — sin esto, SelectorClienteActivo.razor pinta dos
        // <option> idénticas para el mismo tenant, y
        // Ayudas.CambiarClienteActivoAsync (E2E) revienta con "strict mode
        // violation: resolved to 2 elements" (visto en CI, FlujoSoporteTests).
        // El acceso es el mismo workspace, sin importar cuántas delegaciones
        // lo concedan. Distinct()+OrderBy() no se pueden encadenar sobre el
        // IQueryable (EF/Npgsql no traduce esa combinación sobre un record) —
        // el conjunto es minúsculo (delegaciones de un único usuario), así
        // que deduplicar tras materializar es correcto y no cuesta nada.
        resultado.AddRange(delegados.Distinct().OrderBy(c => c.Nombre));

        // Lente de demo Gestor: la lista multi-Tenant de ESE Gestor CAE son los Tenants donde tiene
        // cartera vigente (más el propio, que siempre está autorizado sobre sí mismo). Solo QUITA
        // entradas de la lista real — nunca añade un Tenant que la cuenta no alcanzara ya —, y
        // el endpoint de cambio de Tenant activo sigue autorizando contra las carteras reales.
        if (vistaDemo is not null && await vistaDemo.ObtenerTenantIdsAcotadosAsync(cancellationToken) is { } acotados)
            resultado.RemoveAll(c => !c.EsOrigen && !acotados.Contains(c.TenantId));

        return resultado;
    }
}
