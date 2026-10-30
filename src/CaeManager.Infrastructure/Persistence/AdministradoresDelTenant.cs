using CaeManager.Application.Common;
using CaeManager.Application.Usuarios;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence;

/// <inheritdoc cref="IAdministradoresDelTenant" />
/// <remarks>
/// El candado es el de <c>app_bloquear_administradores_de_tenant</c> —mismo espacio y misma
/// clave—, que toman el trigger <c>app_serializar_alta_de_administrador</c> (asignar el rol o
/// reactivar la cuenta de un Administrador) y el restablecimiento del 2FA por Soporte TALVEG.
/// Se toma con <c>pg_advisory_xact_lock</c> directamente porque la función no tiene EXECUTE
/// para el runtime; la clave tiene que seguir siendo idéntica a la suya.
///
/// <para>
/// La lectura de quién es Administrador es la de
/// <see cref="IDirectorioUsuariosService.ObtenerCuentasActivasConRolAsync"/>: el mismo criterio
/// de cuenta desactivada que el resto del producto, y bajo la RLS de la conexión. Si la RLS no
/// dejara ver otra cuenta del Tenant, contaría de menos y la regla bloquearía: falla cerrada.
/// </para>
/// </remarks>
public class AdministradoresDelTenant(CaeManagerDbContext dbContext, IDirectorioUsuariosService directorio)
    : IAdministradoresDelTenant
{
    private const string Espacio = "app.administradores_de_tenant:";

    public Task BloquearAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "El candado de Administradores solo dura lo que su transacción: tómalo dentro de ITransaccionDeComando.");

        var clave = Espacio + tenantId;
        return dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({clave}, 0))", cancellationToken);
    }

    public async Task<bool> HayOtroAdministradorAsync(
        Guid tenantId, Guid usuarioIdExcluido, CancellationToken cancellationToken = default) =>
        (await directorio.ObtenerCuentasActivasConRolAsync(tenantId, AutoridadSobreCuentas.Administrador, cancellationToken))
        .Any(id => id != usuarioIdExcluido);
}
