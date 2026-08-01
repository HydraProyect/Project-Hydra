using CaeManager.Application.Common;
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
/// </summary>
public class DirectorioUsuariosTenant(
    UserManager<ApplicationUser> userManager, IApplicationDbContext dbContext, ITenantActual tenantActual,
    IPuertaAccesoDatos puertaAccesoDatos)
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
    public Task<IReadOnlyList<ApplicationUser>> ObtenerVisiblesAsync(CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync<IReadOnlyList<ApplicationUser>>(async () =>
        {
            if (tenantActual.TenantId is not { } tenantId) return [];

            var idsDelegados = await ObtenerIdsDeOperadoresDelegadosAsync(tenantId, cancellationToken);

            return await userManager.Users
                .Where(u => u.TenantId == tenantId || idsDelegados.Contains(u.Id))
                .OrderBy(u => u.Email)
                .ToListAsync(cancellationToken);
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

            var idsDelegados = await ObtenerIdsDeOperadoresDelegadosAsync(tenantId, cancellationToken);

            // GetUsersInRoleAsync no se puede componer con LINQ (devuelve una
            // lista ya materializada), así que el filtro se aplica después.
            var enRol = await userManager.GetUsersInRoleAsync(rol);

            return enRol
                .Where(u => u.TenantId == tenantId || idsDelegados.Contains(u.Id))
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

            return (await ObtenerIdsDeOperadoresDelegadosAsync(tenantId, cancellationToken)).Contains(usuarioId);
        }, cancellationToken);

    private async Task<List<Guid>> ObtenerIdsDeOperadoresDelegadosAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await (
            from asignacion in dbContext.AsignacionesOperadorDelegado
            join delegacion in dbContext.DelegacionesTenant on asignacion.DelegacionTenantId equals delegacion.Id
            // Activa y no caducada — ver DelegacionTenant.EstaVigente.
            where delegacion.Activa && delegacion.TenantClienteId == tenantId
                  && (delegacion.ExpiraEnUtc == null || delegacion.ExpiraEnUtc > DateTime.UtcNow)
            select asignacion.UsuarioId)
            .Distinct()
            .ToListAsync(cancellationToken);
}
