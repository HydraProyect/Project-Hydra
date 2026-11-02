using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Components.Account;

/// <summary>
/// La política de contraseña antes de revocar sesiones: los mismos validadores que
/// <c>ResetPasswordAsync</c> y <c>ChangePasswordAsync</c> aplican por dentro. Esas operaciones corren
/// después de la revocación, y una contraseña que Identity va a rechazar no debe cerrar ninguna sesión.
/// </summary>
internal static class ValidacionContrasena
{
    public static async Task<IReadOnlyList<IdentityError>> IncumplidosAsync(
        UserManager<ApplicationUser> usuarios, ApplicationUser usuario, string contrasena)
    {
        var incumplidos = new List<IdentityError>();
        foreach (var validador in usuarios.PasswordValidators)
            incumplidos.AddRange((await validador.ValidateAsync(usuarios, usuario, contrasena)).Errors);
        return incumplidos;
    }
}
