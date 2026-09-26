using CaeManager.Application.Common;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    public async Task<bool> EsAdministradorUnicoActivoAsync(
        Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default) =>
        (await ObtenerAdministradorUnicoActivoAsync(tenantId, cancellationToken))?.UsuarioId == usuarioId;

    public Task<AdministradorUnicoActivo?> ObtenerAdministradorUnicoActivoAsync(
        Guid tenantId, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            // Mismo criterio de «desactivada» que ApplicationUser.EstaDesactivada,
            // traducible a SQL: un bloqueo temporal por intentos fallidos no saca a
            // nadie de la cuenta de Administradores.
            var limiteDesactivada = DateTimeOffset.UtcNow.Add(ApplicationUser.UmbralDeCuentaDesactivada);
            var administradoresActivos = await (
                    from u in contexto.Users
                    where u.TenantId == tenantId
                          && (u.LockoutEnd == null || u.LockoutEnd <= limiteDesactivada)
                          && contexto.UserRoles.Any(ur => ur.UserId == u.Id
                                                          && contexto.Roles.Any(r => r.Id == ur.RoleId
                                                                                     && r.Name == Roles.Administrador))
                    select new AdministradorUnicoActivo(u.Id, u.NombreCompleto, u.Email ?? "", u.TwoFactorEnabled))
                .AsNoTracking()
                .Take(2)
                .ToListAsync(cancellationToken);

            return administradoresActivos.Count == 1 ? administradoresActivos[0] : null;
        }, cancellationToken);

    public Task<Result> RestablecerPorSesionPrivilegiadaAsync(
        Guid sesionPrivilegiadaId, Guid usuarioId, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            // La función es la única puerta de escritura de esta sesión: su conexión
            // lleva cae_app_soporte (solo SELECT), y solo ese rol puede ejecutarla.
            // Devuelve un código en vez de lanzar para que cada negativa llegue con
            // su mensaje; los valores van parametrizados por EF.
            string codigo;
            try
            {
                codigo = await contexto.Database
                    .SqlQuery<string>(
                        $"SELECT app_restablecer_segundo_factor_por_soporte({sesionPrivilegiadaId}, {usuarioId}) AS \"Value\"")
                    .SingleAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                // Contexto RLS firmado inválido o conexión sin el rol de soporte:
                // nunca una escritura, y nunca un 500.
                codigo = "contexto_no_valido";
            }

            // La función escribió por debajo de EF: lo que el circuito tuviera
            // rastreado de esta cuenta y de sus tokens (la clave TOTP y los
            // códigos que acaba de borrar) ya no es cierto.
            DesengancharCuentaYTokens(usuarioId);

            return codigo == "restablecido"
                ? Result.Exito()
                : Result.Fallo(Error.Crear(
                    "SegundoFactor.RestablecimientoPorSoporteDenegado",
                    "La base de datos no autorizó el restablecimiento (" + codigo + "). " +
                    "Comprueba que la sesión sigue abierta y que la cuenta es el Administrador único del Tenant."));
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
        DesengancharCuentaYTokens(usuarioId);
        return await userManager.FindByIdAsync(usuarioId.ToString());
    }

    private void DesengancharCuentaYTokens(Guid usuarioId)
    {
        foreach (var entrada in contexto.ChangeTracker.Entries<ApplicationUser>()
                     .Where(e => e.Entity.Id == usuarioId).ToList())
            entrada.State = EntityState.Detached;

        foreach (var entrada in contexto.ChangeTracker.Entries<IdentityUserToken<Guid>>()
                     .Where(e => e.Entity.UserId == usuarioId).ToList())
            entrada.State = EntityState.Detached;
    }
}
