using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Implementación de <see cref="IAprovisionamientoUsuariosService"/> sobre
/// <see cref="UserManager{TUser}"/>. Mismo mecanismo que
/// <c>Usuarios.razor.cs</c> (token de un solo uso de
/// <c>DataProtectorTokenProvider</c>, correo best-effort, enlace de
/// contingencia si el correo falla) — no se duplica lógica de negocio nueva,
/// solo se abre el mismo camino para que un <c>IComandoDeAprovisionamiento</c>
/// de Application pueda ejecutarlo bajo capacidad <c>Aprovisionamiento</c>.
///
/// <c>urlBaseAbsoluta</c> entra como parámetro en vez de resolverse aquí
/// —Infrastructure no tiene <c>NavigationManager</c>, y esto corre siempre
/// dentro de una petición real de TALVEG, nunca en un job de fondo— la misma
/// razón por la que <c>Usuarios.razor.cs</c> la construye con
/// <c>NavigationManager.ToAbsoluteUri</c> antes de llegar aquí.
/// </summary>
public class AprovisionamientoUsuariosService(UserManager<ApplicationUser> userManager, IEmailService emailService)
    : IAprovisionamientoUsuariosService
{
    private const int MinutosCaducidadActivacion = 60 * 24;

    public async Task<Result<ActivacionUsuarioInicialDto>> CrearUsuarioInicialAsync(
        Guid tenantId, string email, string nombreCompleto, string urlBaseAbsoluta,
        CancellationToken cancellationToken = default)
    {
        var usuario = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = nombreCompleto,
            EmailConfirmed = true,
            TenantId = tenantId,
            DebeCambiarContrasena = false
        };

        // Sin contraseña, igual que el alta ordinaria (Usuarios.razor.cs): la
        // cuenta nace sin ninguna y la persona la establece desde su enlace.
        var resultadoCrear = await userManager.CreateAsync(usuario);
        if (!resultadoCrear.Succeeded)
            return Result.Fallo<ActivacionUsuarioInicialDto>(Error.Crear(
                "UsuarioInicial.NoCreado", DescribirErrores(resultadoCrear)));

        var resultadoRol = await userManager.AddToRoleAsync(usuario, Roles.Administrador);
        if (!resultadoRol.Succeeded)
            return Result.Fallo<ActivacionUsuarioInicialDto>(Error.Crear(
                "UsuarioInicial.SinRol",
                $"La cuenta se creó pero no pudimos asignarle el rol {Roles.Administrador}: {DescribirErrores(resultadoRol)}"));

        return await GenerarEnlaceYRegistrarIntentoAsync(usuario, urlBaseAbsoluta);
    }

    public async Task<Result<ActivacionUsuarioInicialDto>> ReintentarActivacionAsync(
        Guid usuarioId, string urlBaseAbsoluta, CancellationToken cancellationToken = default)
    {
        var usuario = await userManager.FindByIdAsync(usuarioId.ToString());
        if (usuario is null)
            return Result.Fallo<ActivacionUsuarioInicialDto>(
                Error.Crear("UsuarioInicial.NoExiste", "Esta cuenta ya no existe."));

        // Revalida contra la cuenta recién leída, no contra un estado que el
        // llamador pudiera tener cacheado: si la persona completó su
        // activación entretanto, reintentar emitiría un token de
        // restablecimiento válido para una cuenta que ya tiene contraseña
        // (mismo hallazgo que ReenviarCredencialesAsync en Usuarios.razor.cs).
        if (!await EsPendienteActivacionAsync(usuario))
            return Result.Fallo<ActivacionUsuarioInicialDto>(Error.Crear(
                "UsuarioInicial.YaActivado", "Esta cuenta ya no está pendiente de activación."));

        return await GenerarEnlaceYRegistrarIntentoAsync(usuario, urlBaseAbsoluta);
    }

    /// <summary>
    /// Mismo predicado que <c>EsPendienteActivacionAsync</c> en
    /// <c>Usuarios.razor.cs</c>: <c>PasswordHash is null</c> no basta, una
    /// cuenta autoaprovisionada por SSO nace igual de vacía y nunca establece
    /// una.
    /// </summary>
    private async Task<bool> EsPendienteActivacionAsync(ApplicationUser usuario) =>
        string.IsNullOrEmpty(usuario.PasswordHash) && (await userManager.GetLoginsAsync(usuario)).Count == 0;

    private async Task<Result<ActivacionUsuarioInicialDto>> GenerarEnlaceYRegistrarIntentoAsync(
        ApplicationUser usuario, string urlBaseAbsoluta)
    {
        var token = await userManager.GeneratePasswordResetTokenAsync(usuario);
        var tokenCodificado = WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(token));
        var enlace = $"{urlBaseAbsoluta.TrimEnd('/')}/cuenta/restablecer-contrasena?userId={usuario.Id}&code={tokenCodificado}";

        usuario.IntentosActivacion++;
        usuario.UltimoIntentoActivacionUtc = DateTime.UtcNow;
        await userManager.UpdateAsync(usuario);

        var cuerpo = $"""
            <p>Hola {System.Net.WebUtility.HtmlEncode(usuario.NombreCompleto)},</p>
            <p>Se ha creado tu acceso como Administrador de {Marca.Nombre}. Para entrar, establece tu contraseña:</p>
            <p><a href="{System.Net.WebUtility.HtmlEncode(enlace)}">Establecer mi contraseña</a></p>
            <p>El enlace caduca en {MinutosCaducidadActivacion} minutos y solo puede usarse una vez.</p>
            """;

        var resultadoCorreo = await emailService.EnviarAsync(
            usuario.Email!, $"Activa tu acceso a {Marca.Nombre}", cuerpo, TipoAvisoCorreo.Seguridad);

        return Result.Exito(new ActivacionUsuarioInicialDto(
            usuario.Id, enlace, resultadoCorreo.EsExitoso, usuario.IntentosActivacion));
    }

    private static string DescribirErrores(IdentityResult resultado) =>
        string.Join(" ", resultado.Errors.Select(e => e.Description));
}
