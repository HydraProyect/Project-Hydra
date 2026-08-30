using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Autorizacion;

/// <summary>
/// Qué usuarios son visibles desde el tenant activo. Existe porque
/// <c>AspNetUsers</c> es la única tabla sin filtro global —el login necesita
/// resolver el usuario, y con él su tenant, antes de conocerlo (ver
/// <c>CaeManagerDbContext</c>)— y las pantallas llamaban a
/// <c>UserManager.Users</c> / <c>GetUsersInRoleAsync</c> directamente, sin
/// filtrar nada.
///
/// La consecuencia era una fuga real, no teórica: con dos tenants sembrados,
/// un Administrador del tenant #1 veía en <c>/usuarios</c> los 18 usuarios del
/// otro tenant con nombre y correo. Datos personales de empleados de otra
/// organización, en un producto cuyo aislamiento entre tenants es la
/// propiedad de seguridad crítica.
///
/// "Visible" incluye a los Operadores Delegados: un usuario de la Consultora
/// con asignación en una delegación activa sobre este tenant opera aquí y
/// debe poder aparecer como ejecutivo asignable (ADR-004 § 5.3). Filtrar solo
/// por <c>TenantId</c> los habría hecho invisibles en el cliente que
/// justamente gestionan.
///
/// <para>
/// <b>Por qué toma el <c>CaeManagerDbContext</c> además del <c>UserManager</c>.</b>
/// Los recuentos por rol y la lista de cuentas sin rol necesitan
/// <c>AspNetUserRoles</c>, que el <c>UserManager</c> solo deja consultar con
/// <c>GetUsersInRoleAsync</c>/<c>GetRolesAsync</c> — métodos que materializan
/// sin filtrar y no se pueden componer con LINQ, de donde salían las seis
/// consultas globales y el N+1 de <c>/roles</c>. Es una dependencia dentro de
/// Infrastructure, que es donde vive el contexto.
/// </para>
/// </summary>
public class DirectorioUsuariosTenant(
    UserManager<ApplicationUser> userManager, ITenantsQueryContext dbContext, ITenantActual tenantActual,
    PuertaAccesoDatos puertaAccesoDatos, Persistence.CaeManagerDbContext identidad)
    : IDirectorioUsuariosService
{
    /// <summary>
    /// Usuarios del tenant activo, más sus Operadores Delegados. Sin tenant
    /// resuelto devuelve vacío, no todo: mismo fallo cerrado que el resto de
    /// la cadena de resolución.
    ///
    /// Los tres métodos públicos pasan por PuertaAccesoDatos: los llaman
    /// páginas de Blazor directamente (sin MediatR), en paralelo con la
    /// inicialización de los componentes del layout sobre el mismo DbContext
    /// scoped.
    /// </summary>
    /// <summary>
    /// Sin filtro de visibilidad a propósito: la pregunta que responde es "¿de
    /// qué tenant es este usuario?", y quien la hace la necesita justamente
    /// para decidir si ese usuario es aceptable — filtrarla por el tenant
    /// activo la volvería circular. No revela nada: devuelve un Guid de tenant
    /// a partir de un Guid de usuario que el llamante ya tenía.
    /// </summary>
    public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
            await userManager.Users
                .Where(u => u.Id == usuarioId)
                .Select(u => (Guid?)u.TenantId)
                .FirstOrDefaultAsync(cancellationToken));

    public Task<IReadOnlyList<ApplicationUser>> ObtenerVisiblesAsync(CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync<IReadOnlyList<ApplicationUser>>(async () =>
        {
            if (tenantActual.TenantId is not { } tenantId) return [];

            var rolesDelegados = await ObtenerRolesDeOperadoresDelegadosAsync(tenantId, cancellationToken);

            return await userManager.Users
                .Where(u => u.TenantId == tenantId || rolesDelegados.Keys.Contains(u.Id))
                .OrderBy(u => u.Email)
                .ToListAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// Rol acotado (<c>AsignacionOperadorDelegado.Rol</c>) de cada Operador
    /// Delegado visible en el tenant activo — nunca su rol de origen. Un
    /// operador de soporte es <c>Administrador</c> en el tenant de plataforma,
    /// pero <c>CurrentUserService.ObtenerRolActualAsync</c> ya lo acota a
    /// Consulta/GestorCae/CoordinadorCae al operar aquí; mostrar su rol de
    /// origen en /usuarios contradice esa restricción y alarma sin motivo a
    /// quien lo ve (un "Administrador" desconocido en su propia organización).
    /// </summary>
    public Task<IReadOnlyDictionary<Guid, string>> ObtenerRolesDeOperadoresDelegadosAsync(CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync<IReadOnlyDictionary<Guid, string>>(async () =>
        {
            if (tenantActual.TenantId is not { } tenantId) return new Dictionary<Guid, string>();

            return await ObtenerRolesDeOperadoresDelegadosAsync(tenantId, cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// Equivalente a <c>GetUsersInRoleAsync</c> pero acotado al tenant activo
    /// — es el que alimenta los selectores de gestor/ejecutivo.
    /// </summary>
    public Task<IReadOnlyList<ApplicationUser>> ObtenerVisiblesEnRolAsync(
        string rol, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync<IReadOnlyList<ApplicationUser>>(async () =>
        {
            if (tenantActual.TenantId is not { } tenantId) return [];

            var rolesDelegados = await ObtenerRolesDeOperadoresDelegadosAsync(tenantId, cancellationToken);

            // GetUsersInRoleAsync no se puede componer con LINQ (devuelve una
            // lista ya materializada), así que el filtro se aplica después.
            var enRol = await userManager.GetUsersInRoleAsync(rol);

            return enRol
                .Where(u => u.TenantId == tenantId || rolesDelegados.Keys.Contains(u.Id))
                .OrderBy(u => u.NombreCompleto)
                .ToList();
        }, cancellationToken);

    /// <summary>
    /// Para revalidar en servidor un Id que llegó de un selector: que la UI
    /// solo ofrezca opciones válidas no impide escribir otro Guid a mano
    /// (hallazgo N-10 de INFORME-AUDITORIA-2.md).
    /// </summary>
    public Task<bool> EsVisibleEnTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            if (tenantActual.TenantId is not { } tenantId) return false;

            var esDelTenant = await userManager.Users
                .AnyAsync(u => u.Id == usuarioId && u.TenantId == tenantId, cancellationToken);

            if (esDelTenant) return true;

            return (await ObtenerRolesDeOperadoresDelegadosAsync(tenantId, cancellationToken)).ContainsKey(usuarioId);
        }, cancellationToken);

    /// <summary>
    /// Nombres de los usuarios pedidos, acotados igual que el resto del directorio:
    /// del tenant activo o con asignación delegada viva sobre él. Un Id que no pase
    /// ese filtro no aparece en el resultado en vez de resolverse igualmente.
    /// </summary>
    public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(
        IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync<IReadOnlyDictionary<Guid, string>>(async () =>
        {
            if (usuarioIds.Count == 0 || tenantActual.TenantId is not { } tenantId)
                return new Dictionary<Guid, string>();

            var rolesDelegados = await ObtenerRolesDeOperadoresDelegadosAsync(tenantId, cancellationToken);

            var usuarios = await userManager.Users
                .Where(u => usuarioIds.Contains(u.Id) && (u.TenantId == tenantId || rolesDelegados.Keys.Contains(u.Id)))
                .Select(u => new { u.Id, u.NombreCompleto, u.Email })
                .ToListAsync(cancellationToken);

            return usuarios.ToDictionary(
                u => u.Id,
                u => string.IsNullOrWhiteSpace(u.NombreCompleto) ? u.Email ?? "—" : u.NombreCompleto);
        }, cancellationToken);

    /// <summary>
    /// Cuántas cuentas <b>propias</b> del tenant activo tiene cada rol, en una
    /// sola consulta.
    ///
    /// <para>
    /// <b>Propias, no visibles.</b> A diferencia del resto del directorio, aquí
    /// los Operadores Delegados quedan fuera a propósito. Su fila en
    /// <c>AspNetUserRoles</c> guarda el rol de su tenant de ORIGEN, que no es el
    /// que ejercen aquí —eso lo decide su cartera, ver
    /// <c>ObtenerRolesDeOperadoresDelegadosAsync</c>— así que contarlos sumaría
    /// un "Administrador" que nadie es en este tenant. La pantalla de Roles
    /// gobierna las cuentas de esta organización; el rol de un delegado no se
    /// gobierna desde aquí.
    /// </para>
    ///
    /// <para>
    /// Sin tenant resuelto devuelve vacío, no todo: mismo fallo cerrado que el
    /// resto de la cadena.
    /// </para>
    /// </summary>
    public Task<IReadOnlyDictionary<string, int>> ContarCuentasPropiasPorRolAsync(
        CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync<IReadOnlyDictionary<string, int>>(async () =>
        {
            if (tenantActual.TenantId is not { } tenantId) return new Dictionary<string, int>();

            // Una agregación en servidor, no seis materializaciones globales
            // seguidas de un recuento en memoria: el coste deja de crecer con
            // el número total de usuarios del SaaS.
            var porRol = await (
                from usuario in identidad.Users
                where usuario.TenantId == tenantId
                join usuarioRol in identidad.UserRoles on usuario.Id equals usuarioRol.UserId
                join rol in identidad.Roles on usuarioRol.RoleId equals rol.Id
                group usuario by rol.Name into grupo
                select new { Rol = grupo.Key, Cantidad = grupo.Count() })
                .ToListAsync(cancellationToken);

            return porRol
                .Where(x => x.Rol is not null)
                .ToDictionary(x => x.Rol!, x => x.Cantidad, StringComparer.Ordinal);
        }, cancellationToken);

    /// <summary>
    /// Cuentas propias del tenant activo que todavía no tienen ningún rol — la
    /// sala de espera de <c>/roles</c>, que alimenta sobre todo el
    /// autoaprovisionamiento por SSO.
    ///
    /// <para>
    /// Propias por el mismo motivo que <see cref="ContarCuentasPropiasPorRolAsync"/>,
    /// y con un peso añadido: de esta lista sale una <b>escritura</b>. Ofrecer
    /// ahí la cuenta de otra organización sería ofrecer el botón que le cambia
    /// el rol.
    /// </para>
    ///
    /// <para>
    /// La ausencia de rol se resuelve con un <c>NOT EXISTS</c> en servidor. La
    /// versión anterior traía todos los usuarios del sistema y preguntaba por
    /// los roles de cada uno, uno a uno.
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<ApplicationUser>> ObtenerCuentasPropiasSinRolAsync(
        CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync<IReadOnlyList<ApplicationUser>>(async () =>
        {
            if (tenantActual.TenantId is not { } tenantId) return [];

            return await identidad.Users
                .Where(u => u.TenantId == tenantId && !identidad.UserRoles.Any(ur => ur.UserId == u.Id))
                .OrderBy(u => u.FechaCreacion)
                .ToListAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// Si la cuenta pertenece al tenant activo. <b>No</b> es lo mismo que
    /// <see cref="EsVisibleEnTenantActualAsync"/>, y la diferencia es la que
    /// separa leer de mandar: ese predicado da por buenos también a los
    /// Operadores Delegados, que se ven desde aquí pero cuya cuenta pertenece a
    /// otra organización y cuyo rol se gobierna allí. Toda operación que
    /// MODIFIQUE una cuenta tiene que preguntar por esta, no por aquella.
    /// </summary>
    public Task<bool> EsCuentaPropiaDelTenantActualAsync(
        Guid usuarioId, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
            tenantActual.TenantId is { } tenantId
            && await identidad.Users.AnyAsync(
                u => u.Id == usuarioId && u.TenantId == tenantId, cancellationToken));

    private async Task<Dictionary<Guid, string>> ObtenerRolesDeOperadoresDelegadosAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await (
            from asignacion in dbContext.AsignacionesOperadorDelegado
            join delegacion in dbContext.DelegacionesTenant on asignacion.DelegacionTenantId equals delegacion.Id
            // Activa y no caducada — ver DelegacionTenant.EstaVigente.
            where delegacion.Activa && delegacion.TenantClienteId == tenantId
                  && (delegacion.ExpiraEnUtc == null || delegacion.ExpiraEnUtc > DateTime.UtcNow)
            select new { asignacion.UsuarioId, asignacion.Rol })
            .Distinct()
            .ToDictionaryAsync(x => x.UsuarioId, x => x.Rol, cancellationToken);
}
