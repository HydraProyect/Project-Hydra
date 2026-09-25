using CaeManager.Application.Common;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Identity;

/// <inheritdoc cref="ISegundoFactorDeCuentas" />
/// <remarks>
/// Pasa por <see cref="PuertaAccesoDatos"/> como <c>DirectorioUsuariosTenant</c>: los
/// comandos que lo usan se lanzan desde páginas de Blazor, en paralelo con los
/// componentes del layout sobre el mismo <c>CaeManagerDbContext</c> del circuito.
/// </remarks>
public class SegundoFactorDeCuentasIdentity(
    UserManager<ApplicationUser> userManager,
    IUserStore<ApplicationUser> almacen,
    CaeManagerDbContext contexto,
    PuertaAccesoDatos puertaAccesoDatos)
    : ISegundoFactorDeCuentas
{
    public Task<EstadoSegundoFactor?> ObtenerEstadoAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = await CargarEnFrescoAsync(usuarioId);
            if (usuario is null) return null;

            return new EstadoSegundoFactor(
                usuario.TenantId,
                await userManager.GetTwoFactorEnabledAsync(usuario),
                await userManager.CountRecoveryCodesAsync(usuario));
        }, cancellationToken);

    public Task<Result<IReadOnlyList<string>>> GenerarCodigosRecuperacionAsync(
        Guid usuarioId, int cantidad, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = await CargarEnFrescoAsync(usuarioId);
            if (usuario is null)
                return Result.Fallo<IReadOnlyList<string>>(ErrorCuentaNoEncontrada);

            // Identity genera los códigos, llama a AlmacenUsuarios.ReplaceCodesAsync
            // (que guarda solo los hashes) y hace UpdateAsync: una sola escritura, con
            // la comprobación de ConcurrencyStamp de la cuenta.
            var codigos = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(usuario, cantidad);
            if (codigos is null)
                return Result.Fallo<IReadOnlyList<string>>(Error.Crear(
                    "SegundoFactor.CodigosNoGuardados",
                    "No pudimos guardar los códigos de recuperación. Inténtalo de nuevo."));

            return Result.Exito<IReadOnlyList<string>>(codigos.ToList());
        }, cancellationToken);

    public Task<Result> RestablecerAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = await CargarEnFrescoAsync(usuarioId);
            if (usuario is null) return Result.Fallo(ErrorCuentaNoEncontrada);

            var dosFactores = (IUserTwoFactorStore<ApplicationUser>)almacen;
            var tokens = (IUserAuthenticationTokenStore<ApplicationUser>)almacen;
            var sello = (IUserSecurityStampStore<ApplicationUser>)almacen;

            // Todo sobre el almacén, sin guardar, y un único UpdateAsync al final:
            // los métodos equivalentes de UserManager guardan cada uno por su cuenta,
            // y un fallo a medias dejaría, por ejemplo, la 2FA desactivada con los
            // códigos antiguos todavía válidos para la próxima vez que se active.
            await dosFactores.SetTwoFactorEnabledAsync(usuario, false, cancellationToken);
            await tokens.RemoveTokenAsync(
                usuario, AlmacenUsuarios.ProveedorInterno, NombreTokenClaveAutenticador, cancellationToken);
            await tokens.RemoveTokenAsync(
                usuario, AlmacenUsuarios.ProveedorInterno, AlmacenUsuarios.NombreTokenCodigosRecuperacion, cancellationToken);
            // Sello nuevo: SignInManagerCuentaDesactivada.ValidateSecurityStampAsync
            // cierra en su siguiente validación las sesiones abiertas de la cuenta,
            // incluida la de quien tuviera el móvil perdido.
            await sello.SetSecurityStampAsync(usuario, Guid.NewGuid().ToString("N").ToUpperInvariant(), cancellationToken);

            var resultado = await userManager.UpdateAsync(usuario);
            return resultado.Succeeded
                ? Result.Exito()
                : Result.Fallo(Error.Crear(
                    "SegundoFactor.RestablecimientoNoGuardado",
                    "No pudimos restablecer la verificación en dos pasos. Vuelve a cargar la página e inténtalo de nuevo."));
        }, cancellationToken);

    // El nombre que usa UserStoreBase para la clave TOTP (constante privada allí).
    private const string NombreTokenClaveAutenticador = "AuthenticatorKey";

    private static readonly Error ErrorCuentaNoEncontrada =
        Error.Crear("SegundoFactor.CuentaNoEncontrada", "No encontramos esa cuenta.");

    /// <summary>
    /// <c>FindByIdAsync</c> devuelve la entidad ya rastreada si el circuito la
    /// cargó antes (por ejemplo, la lista de /usuarios), sin refrescarla: su
    /// <c>ConcurrencyStamp</c> viejo haría fallar la escritura, o, peor, la
    /// decisión se tomaría sobre un estado de 2FA caducado. Se desenganchan la
    /// cuenta y sus tokens antes de cargarla.
    /// </summary>
    private async Task<ApplicationUser?> CargarEnFrescoAsync(Guid usuarioId)
    {
        foreach (var entrada in contexto.ChangeTracker.Entries<ApplicationUser>()
                     .Where(e => e.Entity.Id == usuarioId).ToList())
            entrada.State = EntityState.Detached;

        foreach (var entrada in contexto.ChangeTracker.Entries<IdentityUserToken<Guid>>()
                     .Where(e => e.Entity.UserId == usuarioId).ToList())
            entrada.State = EntityState.Detached;

        return await userManager.FindByIdAsync(usuarioId.ToString());
    }
}
