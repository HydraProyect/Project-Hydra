using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Hash de una contraseña aleatoria, calculado una vez con el <see cref="IPasswordHasher{TUser}"/>
/// configurado, para verificarlo cuando el inicio con contraseña no llega a comparar el hash de una
/// cuenta (no existe, está bloqueada, no puede iniciar sesión o no tiene contraseña local). Así el
/// coste dominante de la respuesta, el del hash, es el de una contraseña errónea y no delata qué
/// cuentas existen (S-A-10). Quedan diferencias de pocos milisegundos (registro del intento,
/// contador de fallos) muy por debajo de ese coste.
/// </summary>
public sealed class VerificacionContrasenaFicticia
{
    private readonly ApplicationUser _cuenta = new();
    private readonly string _hash;

    public VerificacionContrasenaFicticia(IPasswordHasher<ApplicationUser> hasher)
    {
        _hash = hasher.HashPassword(_cuenta, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public void Verificar(IPasswordHasher<ApplicationUser> hasher, string contrasena) =>
        hasher.VerifyHashedPassword(_cuenta, _hash, contrasena);
}
