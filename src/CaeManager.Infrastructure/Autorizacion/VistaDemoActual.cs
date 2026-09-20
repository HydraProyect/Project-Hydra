using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.VistaDemo;
using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Autorizacion;

/// <summary>
/// Activación de la lente de demo. Apagada por defecto: en un despliegue real no hay
/// sección <c>VistaDemo</c> y el selector no existe.
/// </summary>
public class VistaDemoOptions
{
    public const string SeccionConfiguracion = "VistaDemo";

    public bool Activo { get; set; }
}

/// <summary>
/// Valida la vista pedida por la cookie y decide si se aplica. Es el ÚNICO sitio que decide
/// "esta lente vale"; <see cref="AlcanceDatosService"/> solo pregunta y luego INTERSECA con su
/// resultado real, de modo que ni un fallo de esta clase puede ampliar un alcance: el peor caso
/// de una respuesta equivocada es que la lente estreche de más o no estreche.
///
/// <para>
/// <b>Qué comprueba, cada vez, antes de aplicar nada</b> (fallo cerrado = sin lente = la
/// autorización real sin acotar):
/// activación por configuración; que no sea una sesión privilegiada de plataforma (sin rol de
/// negocio no hay lente); que el rol del token de sesión sea Administrador o DireccionCae (la
/// máxima autoridad legítima de la cuenta de demo — una cuenta sin ese rol no tiene qué
/// estrechar); que el Tenant de ORIGEN de la cuenta sea de demo (allowlist exacta
/// <see cref="RetiradaTenantDemoService.NombresTenantsDeDemo"/>, distinto del de plataforma) y
/// que el Tenant ACTIVO también lo sea; y, para la vista Gestor, que el Gestor pedido sea uno
/// de los del Operador CAE de la cuenta con Asignación de Cartera vigente y rol GestorCae.
/// </para>
///
/// <para>
/// La disponibilidad del selector (<see cref="EstaDisponibleAsync"/>) depende solo de la
/// identidad y del Tenant de origen, nunca del Tenant activo ni de la vista aplicada: una vista
/// no puede esconder el selector que sirve para salir de ella.
/// </para>
///
/// <para>
/// Scoped, y memoiza por circuito de Blazor igual que <see cref="AlcanceDatosService"/>: cambiar
/// de vista es un POST + recarga completa (ver <c>/cuenta/vista-demo</c>), que crea circuito y
/// scope nuevos y por tanto recalcula todo. El resultado por Tenant se indexa por Tenant activo
/// porque el fan-out multi-Tenant reutiliza esta misma instancia para varios Tenants.
/// </para>
/// </summary>
public class VistaDemoActual(
    CaeManagerDbContext dbContext,
    ICurrentUserService currentUserService,
    ITenantActual tenantActual,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ISolicitudVistaDemo solicitud,
    IOptions<VistaDemoOptions> opciones) : IVistaDemoActual
{
    private bool? _disponible;
    private Guid? _tenantOrigenId;
    private HashSet<Guid>? _tenantsDeDemo;
    private IReadOnlyList<GestorDeVistaDemo>? _gestoresElegibles;
    private readonly Dictionary<Guid, VistaDemoEfectiva?> _efectivaPorTenant = new();
    private readonly Dictionary<Guid, IReadOnlyList<Guid>?> _tenantsAcotadosPorTenant = new();

    public async Task<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default)
    {
        if (_disponible is { } cacheado) return cacheado;

        var disponible = await CalcularDisponibleAsync(cancellationToken);
        _disponible = disponible;
        return disponible;
    }

    private async Task<bool> CalcularDisponibleAsync(CancellationToken cancellationToken)
    {
        if (!opciones.Value.Activo) return false;

        // Sin rol de negocio no hay lente: una sesión privilegiada de plataforma (plano 3) entra
        // por otra vía y nunca es Gestor ni Operador CAE (ADR-011).
        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not null) return false;

        if ((await solicitud.ObtenerAsync()).RolDeSesion is not (Roles.Administrador or Roles.DireccionCae)) return false;

        if (await currentUserService.ObtenerUsuarioActualIdAsync() is null) return false;
        if (await currentUserService.ObtenerTenantOrigenIdAsync() is not { } origen) return false;

        var tenantsDeDemo = await ObtenerTenantsDeDemoAsync(cancellationToken);
        if (!tenantsDeDemo.Contains(origen)) return false;

        _tenantOrigenId = origen;
        return true;
    }

    public async Task<VistaDemoEfectiva?> ObtenerEfectivaAsync(CancellationToken cancellationToken = default)
    {
        var clave = tenantActual.TenantId ?? Guid.Empty;
        if (_efectivaPorTenant.TryGetValue(clave, out var cacheada)) return cacheada;

        var efectiva = await CalcularEfectivaAsync(cancellationToken);
        _efectivaPorTenant[clave] = efectiva;
        return efectiva;
    }

    private async Task<VistaDemoEfectiva?> CalcularEfectivaAsync(CancellationToken cancellationToken)
    {
        var peticion = await solicitud.ObtenerAsync();
        if (peticion.Vista is not { } vista) return null;
        if (!await EstaDisponibleAsync(cancellationToken)) return null;

        // "Tenants de demo únicamente": la lente no actúa en ningún Tenant que no sea de demo.
        if (tenantActual.TenantId is not { } activo
            || !(await ObtenerTenantsDeDemoAsync(cancellationToken)).Contains(activo))
            return null;

        switch (vista)
        {
            case VistaDemo.Direccion:
            case VistaDemo.CoordinadorCae:
                return new VistaDemoEfectiva(vista, null);

            case VistaDemo.GestorCae:
                if (peticion.GestorUsuarioId is not { } gestorId) return null;
                var elegibles = await ObtenerGestoresElegiblesAsync(cancellationToken);
                return elegibles.Any(g => g.UsuarioId == gestorId)
                    ? new VistaDemoEfectiva(VistaDemo.GestorCae, gestorId)
                    : null;

            default:
                return null;
        }
    }

    public async Task<IReadOnlyList<GestorDeVistaDemo>> ObtenerGestoresElegiblesAsync(CancellationToken cancellationToken = default)
    {
        if (_gestoresElegibles is not null) return _gestoresElegibles;
        if (!await EstaDisponibleAsync(cancellationToken) || _tenantOrigenId is not { } origen)
            return _gestoresElegibles = [];

        var ahora = DateTime.UtcNow;

        var conCartera = await dbContext.AsignacionesCartera
            .Where(c => c.Estado == EstadoAsignacion.Vigente
                        && c.VigenciaDesde <= ahora
                        && (c.VigenciaHasta == null || ahora < c.VigenciaHasta))
            .Join(dbContext.AsignacionesOperacion.Where(o =>
                    o.OperadorTenantId == origen
                    && o.Estado == EstadoAsignacion.Vigente
                    && o.VigenciaDesde <= ahora
                    && (o.VigenciaHasta == null || ahora < o.VigenciaHasta)),
                c => c.AsignacionOperacionId, o => o.Id, (c, o) => c.UsuarioId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (conCartera.Count == 0) return _gestoresElegibles = [];

        // Solo Gestores CAE del propio Operador: es el rol cuyo alcance calcula
        // AlcanceDatosService por cartera, y el único que la lente sabe reproducir con fidelidad.
        var gestores = await (
            from u in dbContext.Users
            join ur in dbContext.UserRoles on u.Id equals ur.UserId
            join r in dbContext.Roles on ur.RoleId equals r.Id
            where conCartera.Contains(u.Id) && u.TenantId == origen && r.Name == Roles.GestorCae
            select new GestorDeVistaDemo(u.Id, u.NombreCompleto))
            .Distinct()
            .ToListAsync(cancellationToken);

        return _gestoresElegibles = gestores.OrderBy(g => g.Nombre, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.UsuarioId).ToList();
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerTenantIdsAcotadosAsync(CancellationToken cancellationToken = default)
    {
        var clave = tenantActual.TenantId ?? Guid.Empty;
        if (_tenantsAcotadosPorTenant.TryGetValue(clave, out var cacheado)) return cacheado;

        IReadOnlyList<Guid>? resultado = null;
        if (await ObtenerEfectivaAsync(cancellationToken) is { Vista: VistaDemo.GestorCae, GestorUsuarioId: { } gestorId }
            && _tenantOrigenId is { } origen)
        {
            var ahora = DateTime.UtcNow;
            resultado = await dbContext.AsignacionesCartera
                .Where(c => c.UsuarioId == gestorId
                            && c.Estado == EstadoAsignacion.Vigente
                            && c.VigenciaDesde <= ahora
                            && (c.VigenciaHasta == null || ahora < c.VigenciaHasta))
                .Join(dbContext.AsignacionesOperacion.Where(o =>
                        o.OperadorTenantId == origen
                        && o.Estado == EstadoAsignacion.Vigente
                        && o.VigenciaDesde <= ahora
                        && (o.VigenciaHasta == null || ahora < o.VigenciaHasta)),
                    c => c.AsignacionOperacionId, o => o.Id, (c, o) => o.PropietarioTenantId)
                .Distinct()
                .ToListAsync(cancellationToken);
        }

        _tenantsAcotadosPorTenant[clave] = resultado;
        return resultado;
    }

    private async Task<HashSet<Guid>> ObtenerTenantsDeDemoAsync(CancellationToken cancellationToken)
    {
        if (_tenantsDeDemo is not null) return _tenantsDeDemo;

        // Allowlist exacta por nombre (la misma que RetiradaTenantDemoService) y nunca el
        // Tenant de plataforma. Tenants no lleva RLS ni filtro (es el catálogo raíz).
        var ids = await dbContext.Tenants
            .Where(t => !t.EsPlataforma && RetiradaTenantDemoService.NombresTenantsDeDemo.Contains(t.Nombre))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        return _tenantsDeDemo = ids.ToHashSet();
    }
}
