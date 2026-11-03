using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Autorizacion;

/// <summary>Lo que <c>/delegaciones</c> enseña de una persona con acceso, sin credenciales (ASVS 4.0.3 V4.1.3).</summary>
/// <param name="Id">La cuenta.</param>
/// <param name="NombreCompleto">Su nombre.</param>
/// <param name="Email">Su correo.</param>
/// <param name="Rol">
/// Su rol de Identity, el del Tenant de origen: un rol por cuenta y, si hubiera dos, uno estable. Se enseña; no autoriza.
/// </param>
public sealed record PersonaDelegadaVisible(Guid Id, string NombreCompleto, string? Email, string? Rol);

/// <summary>
/// Lee por lote las cuentas de las personas de <c>/delegaciones</c> proyectando columnas, sin materializar
/// <see cref="Identity.ApplicationUser"/>: en una Sesión Privilegiada la conexión lleva <c>cae_app_soporte</c>, y una
/// cuenta completa arrastraría también <c>PasswordHash</c> y <c>SecurityStamp</c>.
/// </summary>
public interface ILecturaPersonasDelegadas
{
    /// <summary>
    /// Las cuentas pedidas que esta conexión ve. La que no ve no aparece y no se distingue de una inexistente: el
    /// filtro es la política <c>cuentas_lectura</c> de <c>AspNetUsers</c> con el contexto de la conexión, el mismo que
    /// recibe <c>UserManager.FindByIdAsync</c>, porque ambos comparten el <c>CaeManagerDbContext</c> del ámbito.
    /// </summary>
    Task<IReadOnlyList<PersonaDelegadaVisible>> ObtenerAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}

/// <summary>
/// Implementación de <see cref="ILecturaPersonasDelegadas"/>. Sin rastreo y en una sola sentencia: los roles cuelgan de
/// la fila de <c>AspNetUsers</c> que la política dejó pasar; la política de <c>AspNetUserRoles</c> aplica
/// esa misma visibilidad para lectura. Comparte contexto scoped, así que desde un circuito
/// de Blazor se llama dentro de <c>PuertaAccesoDatos</c>.
/// </summary>
public sealed class LecturaPersonasDelegadas(CaeManagerDbContext db) : ILecturaPersonasDelegadas
{
    public async Task<IReadOnlyList<PersonaDelegadaVisible>> ObtenerAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return [];

        var cuentas = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new
            {
                u.Id,
                u.NombreCompleto,
                u.Email,
                Roles = db.UserRoles
                    .Where(ur => ur.UserId == u.Id)
                    .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        return cuentas
            .Select(u => new PersonaDelegadaVisible(
                u.Id, u.NombreCompleto, u.Email, u.Roles.OfType<string>().Order(StringComparer.Ordinal).FirstOrDefault()))
            .ToList();
    }
}
