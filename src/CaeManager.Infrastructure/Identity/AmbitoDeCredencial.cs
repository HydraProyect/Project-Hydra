using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Ámbito de servicios propio para mirar la cuenta dentro del cerrojo de credencial, como
/// <c>VigilanciaInicioSesionService</c>. La cuenta que la página leyó antes del cerrojo sigue rastreada en
/// el contexto de la petición: allí <c>FindByIdAsync</c> devolvería esa misma instancia con sus sellos
/// antiguos, así que un enlace ya gastado seguiría verificando y no se notaría que la cuenta cambió. Al
/// restablecer una contraseña, la revocación va en este mismo ámbito porque rotar el sello con la instancia
/// antigua chocaría con el cambio de contraseña.
/// </summary>
public sealed class AmbitoDeCredencial : IAsyncDisposable
{
    private readonly AsyncServiceScope _ambito;
    private readonly UserManager<ApplicationUser> _usuarios;

    private AmbitoDeCredencial(AsyncServiceScope ambito)
    {
        _ambito = ambito;
        _usuarios = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Sesiones = ambito.ServiceProvider.GetRequiredService<IRegistroSesionesUsuario>();
    }

    public IRegistroSesionesUsuario Sesiones { get; }

    public static AmbitoDeCredencial Abrir(IServiceScopeFactory ambitos) => new(ambitos.CreateAsyncScope());

    /// <summary>
    /// ¿Sigue la cuenta en la base tal como la leyó <paramref name="leida"/>? Mira los dos sellos: el de
    /// seguridad, que rota un cambio de credencial, y el de concurrencia, que cambia con cualquier escritura
    /// de Identity y con el que <c>ChangePasswordAsync</c> fallaría al guardar. <see langword="false"/> también
    /// si la cuenta ya no existe. Se lee sin depender del Tenant de este ámbito, como
    /// <c>RegistroSesionesUsuario</c>.
    /// </summary>
    public async Task<bool> SigueComoSeLeyoAsync(ApplicationUser leida)
    {
        ApplicationUser? cuenta;
        using (AmbitoIdentificacionSinTenant.Abrir())
            cuenta = await _usuarios.FindByIdAsync(leida.Id.ToString());

        return cuenta is not null
            && cuenta.SecurityStamp == leida.SecurityStamp
            && cuenta.ConcurrencyStamp == leida.ConcurrencyStamp;
    }

    /// <summary>
    /// La cuenta tal como está en la base, o <see langword="null"/> si no existe o el enlace de
    /// restablecimiento ya no vale para ella: entonces no se revoca nada.
    /// </summary>
    public async Task<ApplicationUser?> ReleerSiElEnlaceValeAsync(Guid usuarioId, string token)
    {
        var cuenta = await _usuarios.FindByIdAsync(usuarioId.ToString());
        if (cuenta is null)
            return null;

        var vale = await _usuarios.VerifyUserTokenAsync(
            cuenta, _usuarios.Options.Tokens.PasswordResetTokenProvider,
            UserManager<ApplicationUser>.ResetPasswordTokenPurpose, token);
        return vale ? cuenta : null;
    }

    /// <summary>Con la instancia que devolvió <see cref="ReleerSiElEnlaceValeAsync"/>.</summary>
    public Task<IdentityResult> RestablecerAsync(ApplicationUser cuenta, string token, string contrasenaNueva) =>
        _usuarios.ResetPasswordAsync(cuenta, token, contrasenaNueva);

    public ValueTask DisposeAsync() => _ambito.DisposeAsync();
}
