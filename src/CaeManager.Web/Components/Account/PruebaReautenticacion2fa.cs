using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Web.Components.Account;

/// <summary>
/// Prueba de que la persona se acaba de reautenticar para dar de alta el TOTP (ASVS 4.0.3 V2.5 y
/// V3.7.1). La página del alta es SSR estática: el POST del código no ve lo que se decidió al
/// pintarla, así que la reautenticación viaja en un campo oculto protegido con Data Protection, con
/// vigencia limitada y ligada a la cuenta y a la sesión de cookie (<c>talveg:sid</c>). Una prueba
/// de otra cuenta, de otra sesión, caducada o manipulada no vale.
/// </summary>
public static class PruebaReautenticacion2fa
{
    /// <summary>
    /// El tiempo de instalar la aplicación y escanear el QR. La ventana del inicio de sesión
    /// reciente es aparte.
    /// </summary>
    public static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(15);

    private const string Proposito = "CaeManager.Web.ConfigurarAutenticadorDosFactores.Reautenticacion.v1";

    public static string Emitir(IDataProtectionProvider proveedor, Guid usuarioId, Guid? sid, DateTimeOffset ahora) =>
        Protector(proveedor).Protect(Contenido(usuarioId, sid), ahora + Vigencia);

    public static bool EsValida(IDataProtectionProvider proveedor, string? prueba, Guid usuarioId, Guid? sid)
    {
        if (string.IsNullOrEmpty(prueba))
            return false;
        try
        {
            return string.Equals(Protector(proveedor).Unprotect(prueba, out _), Contenido(usuarioId, sid), StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider proveedor) =>
        proveedor.CreateProtector(Proposito).ToTimeLimitedDataProtector();

    private static string Contenido(Guid usuarioId, Guid? sid) => $"{usuarioId:N}|{sid?.ToString("N")}";
}
