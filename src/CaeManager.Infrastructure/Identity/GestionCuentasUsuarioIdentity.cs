using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Autorizacion;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace CaeManager.Infrastructure.Identity;

/// <inheritdoc cref="IGestionCuentasUsuario" />
/// <remarks>
/// Pasa por <see cref="PuertaAccesoDatos"/> como <see cref="SegundoFactorDeCuentasIdentity"/>:
/// los Commands que lo usan se lanzan desde páginas de Blazor, en paralelo con los
/// componentes del layout sobre el mismo <c>CaeManagerDbContext</c> del circuito.
/// Las dos lecturas de directorio son virtuales para que los tests de página las
/// sustituyan sin base de datos.
/// </remarks>
public class GestionCuentasUsuarioIdentity(
    UserManager<ApplicationUser> userManager,
    PuertaAccesoDatos puertaAccesoDatos,
    DirectorioUsuariosTenant directorio)
    : IGestionCuentasUsuario
{
    private static readonly Error ErrorCuentaNoEncontrada =
        Error.Crear("Usuarios.NoEncontrado", "No encontramos este usuario.");

    public Task<CuentaUsuario?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = await userManager.FindByIdAsync(usuarioId.ToString());
            if (usuario is null) return null;

            var roles = await userManager.GetRolesAsync(usuario);
            var pendiente = string.IsNullOrEmpty(usuario.PasswordHash)
                            && (await userManager.GetLoginsAsync(usuario)).Count == 0;

            return new CuentaUsuario(
                usuario.Id,
                usuario.Email ?? string.Empty,
                usuario.NombreCompleto,
                await EsPropiaDelTenantActualAsync(usuarioId, cancellationToken),
                roles.ToList(),
                pendiente,
                !usuario.EstaDesactivada(DateTimeOffset.UtcNow),
                usuario.PermisoConsultarAccesoDocumentosSensibles,
                usuario.CoordinadorUsuarioId,
                usuario.ClienteId);
        }, cancellationToken);

    public virtual Task<bool> TieneVinculoOperativoAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        directorio.TieneVinculoOperativoAsync(usuarioId, cancellationToken);

    /// <summary>
    /// Propiedad, no visibilidad: un Operador CAE externo delegado se ve en la lista
    /// del Tenant propietario, pero su cuenta es de su organización (ver
    /// <see cref="DirectorioUsuariosTenant.EsCuentaPropiaDelTenantActualAsync"/>).
    /// </summary>
    public virtual Task<bool> EsPropiaDelTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        directorio.EsCuentaPropiaDelTenantActualAsync(usuarioId, cancellationToken);

    public Task<Result<Guid>> CrearAsync(NuevaCuentaUsuario cuenta, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = new ApplicationUser
            {
                UserName = cuenta.Email,
                Email = cuenta.Email,
                NombreCompleto = cuenta.NombreCompleto,
                EmailConfirmed = true,
                // ApplicationUser no lo sella el interceptor de tenant: todo usuario
                // nuevo se crea con un TenantId explícito.
                TenantId = cuenta.TenantId,
                CoordinadorUsuarioId = cuenta.CoordinadorUsuarioId,
                ClienteId = cuenta.ClienteId,
                PermisoConsultarAccesoDocumentosSensibles = cuenta.PermisoConsultarAccesoDocumentosSensibles,
                // La cuenta nace sin contraseña: la establece su titular desde el
                // enlace de activación, así que no hay una ajena que obligar a cambiar.
                DebeCambiarContrasena = false,
            };

            var resultado = await userManager.CreateAsync(usuario);
            return resultado.Succeeded
                ? Result.Exito(usuario.Id)
                : Result.Fallo<Guid>(ErrorDeIdentity("Usuarios.FalloAlCrear", resultado));
        }, cancellationToken);

    public Task<Result> AsignarRolAsync(Guid usuarioId, string rol, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = await userManager.FindByIdAsync(usuarioId.ToString());
            if (usuario is null) return Result.Fallo(ErrorCuentaNoEncontrada);

            var resultado = await userManager.AddToRoleAsync(usuario, rol);
            return resultado.Succeeded ? Result.Exito() : Result.Fallo(ErrorDeIdentity("Usuarios.FalloAlAsignarRol", resultado));
        }, cancellationToken);

    public Task<Result> ActualizarDatosAsync(Guid usuarioId, DatosCuentaUsuario datos, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = await userManager.FindByIdAsync(usuarioId.ToString());
            if (usuario is null) return Result.Fallo(ErrorCuentaNoEncontrada);

            usuario.NombreCompleto = datos.NombreCompleto;
            usuario.CoordinadorUsuarioId = datos.CoordinadorUsuarioId;
            usuario.ClienteId = datos.ClienteId;
            usuario.PermisoConsultarAccesoDocumentosSensibles = datos.PermisoConsultarAccesoDocumentosSensibles;

            var resultado = await userManager.UpdateAsync(usuario);
            return resultado.Succeeded ? Result.Exito() : Result.Fallo(ErrorDeIdentity("Usuarios.FalloAlActualizar", resultado));
        }, cancellationToken);

    public Task<ResultadoCambioRol> CambiarRolAsync(Guid usuarioId, string rolNuevo, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = await userManager.FindByIdAsync(usuarioId.ToString());
            if (usuario is null) return new ResultadoCambioRol(DesenlaceCambioRol.NoEncontrada);

            var rolesActuales = await userManager.GetRolesAsync(usuario);

            // No se intenta AddToRoleAsync sobre un Remove que no llegó a
            // completarse: el usuario conserva su rol anterior, que es un estado
            // válido, en vez de arriesgar dos roles a la vez (la invariante es
            // exactamente uno).
            var quitar = await userManager.RemoveFromRolesAsync(usuario, rolesActuales);
            if (!quitar.Succeeded)
                return new ResultadoCambioRol(DesenlaceCambioRol.FalloAlQuitarConservaElAnterior, DescribirErrores(quitar));

            // Si esto falla la cuenta queda sin rol. Identity no ofrece la pareja
            // como transacción: no se inventa una compensación, se dice tal cual.
            var poner = await userManager.AddToRoleAsync(usuario, rolNuevo);
            return poner.Succeeded
                ? new ResultadoCambioRol(DesenlaceCambioRol.Cambiado)
                : new ResultadoCambioRol(DesenlaceCambioRol.FalloAlPonerQuedaSinRol, DescribirErrores(poner));
        }, cancellationToken);

    public Task<Result> CambiarActivacionAsync(Guid usuarioId, bool activar, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = await userManager.FindByIdAsync(usuarioId.ToString());
            if (usuario is null) return Result.Fallo(ErrorCuentaNoEncontrada);

            // Desactivar rota además el security stamp, en la misma escritura (ver
            // ApplicationUser.Desactivar): la cookie y el circuito ya abiertos
            // seguían leyendo y escribiendo con la cuenta desactivada.
            if (activar)
                usuario.Reactivar();
            else
                usuario.Desactivar();

            var resultado = await userManager.UpdateAsync(usuario);
            return resultado.Succeeded ? Result.Exito() : Result.Fallo(ErrorDeIdentity("Usuarios.FalloAlCambiarActivacion", resultado));
        }, cancellationToken);

    public Task<Result> EliminarAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = await userManager.FindByIdAsync(usuarioId.ToString());
            if (usuario is null) return Result.Fallo(ErrorCuentaNoEncontrada);

            var resultado = await userManager.DeleteAsync(usuario);
            return resultado.Succeeded ? Result.Exito() : Result.Fallo(ErrorDeIdentity("Usuarios.FalloAlEliminar", resultado));
        }, cancellationToken);

    public Task<Result<string>> GenerarTokenActivacionAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = await userManager.FindByIdAsync(usuarioId.ToString());
            if (usuario is null) return Result.Fallo<string>(ErrorCuentaNoEncontrada);

            var token = await userManager.GeneratePasswordResetTokenAsync(usuario);
            return Result.Exito(WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token)));
        }, cancellationToken);

    /// <summary>El motivo que da Identity, en una sola línea legible.</summary>
    private static string DescribirErrores(IdentityResult resultado) =>
        string.Join(" ", resultado.Errors.Select(e => e.Description));

    private static Error ErrorDeIdentity(string codigo, IdentityResult resultado) =>
        Error.Crear(codigo, DescribirErrores(resultado));
}
