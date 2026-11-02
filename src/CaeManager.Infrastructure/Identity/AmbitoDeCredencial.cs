using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Ámbito de servicios propio para restablecer una contraseña dentro del cerrojo de credencial, como
/// <c>VigilanciaInicioSesionService</c>. La cuenta que la página leyó antes del cerrojo sigue rastreada en
/// el contexto de la petición: allí <c>FindByIdAsync</c> devolvería esa misma instancia con su sello antiguo
/// y un enlace ya gastado seguiría verificando. La revocación va en este mismo ámbito porque rotar el sello
/// con la instancia antigua chocaría con el cambio de contraseña.
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
