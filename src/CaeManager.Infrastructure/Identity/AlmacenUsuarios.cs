using System.Security.Cryptography;
using System.Text;
using CaeManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// El almacén de usuarios de Identity con una sola diferencia: los códigos de
/// recuperación de la verificación en dos pasos se guardan con hash, no en claro
/// (P0-8 del plan de madurez 09-24, hallazgo FS-01).
///
/// <para>
/// <b>Por qué hace falta.</b> El <c>UserStoreBase</c> de ASP.NET Core Identity
/// guarda los códigos tal cual, unidos por <c>;</c>, en la fila
/// <c>[AspNetUserStore]/RecoveryCodes</c> de <c>AspNetUserTokens</c>. Un código de
/// recuperación sustituye a la aplicación autenticadora: quien lea esa fila
/// (una copia de seguridad, un volcado, una consulta con el rol de mantenimiento)
/// tendría el segundo factor de cualquier cuenta. La auditoría ya enmascara el
/// valor (<c>AuditoriaInterceptor.PropiedadesSensiblesPorTipo</c>), pero eso cubre
/// el historial, no la fila viva.
/// </para>
///
/// <para>
/// <b>Qué cambia.</b> Solo los tres métodos de <c>IUserTwoFactorRecoveryCodeStore</c>.
/// <c>UserManager.GenerateNewTwoFactorRecoveryCodesAsync</c> genera los códigos y
/// se los devuelve al llamante una sola vez; aquí llegan en claro y se guardan
/// como PBKDF2 con sal propia por código. Canjear uno lo compara con cada hash y
/// lo quita de la lista: sirve una sola vez. La fila sigue en la misma tabla y
/// con el mismo nombre, así que el enmascarado de la auditoría y el borrado al
/// restablecer (<c>RemoveAuthenticationTokenAsync</c>) siguen valiendo igual.
/// </para>
///
/// <para>
/// Una fila con el formato antiguo (en claro) no se acepta: no se contó ningún
/// código de recuperación antes de este almacén (FS-01 midió cero usos de
/// <c>GenerateNewTwoFactorRecoveryCodesAsync</c> en <c>src</c>), y aceptar el
/// formato en claro sería mantener viva justo la debilidad que esto cierra.
/// </para>
/// </summary>
public class AlmacenUsuarios(CaeManagerDbContext context, IdentityErrorDescriber? describer = null)
    : UserStore<ApplicationUser, IdentityRole<Guid>, CaeManagerDbContext, Guid>(context, describer)
{
    // Los mismos valores que las constantes privadas de UserStoreBase: la fila
    // es la misma que usaría Identity sin este almacén.
    internal const string ProveedorInterno = "[AspNetUserStore]";
    internal const string NombreTokenCodigosRecuperacion = "RecoveryCodes";

    private const string PrefijoFormato = "pbkdf2-sha256$";
    private const int Iteraciones = 100_000;
    private const int BytesSal = 16;
    private const int BytesHash = 32;

    public override Task ReplaceCodesAsync(
        ApplicationUser user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(recoveryCodes);

        var hashes = recoveryCodes.Select(CalcularHash);
        return GuardarHashesAsync(user, hashes, cancellationToken);
    }

    public override async Task<bool> RedeemCodeAsync(
        ApplicationUser user, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(code);

        var hashes = await LeerHashesAsync(user, cancellationToken);
        var normalizado = Normalizar(code);
        if (normalizado.Length == 0) return false;

        var coincidente = hashes.FirstOrDefault(h => Coincide(h, normalizado));
        if (coincidente is null) return false;

        await GuardarHashesAsync(user, hashes.Where(h => !ReferenceEquals(h, coincidente)), cancellationToken);
        return true;
    }

    public override async Task<int> CountCodesAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return (await LeerHashesAsync(user, cancellationToken)).Count;
    }

    /// <summary>
    /// Mayúsculas, sin espacios ni guiones: los códigos que genera Identity son
    /// <c>XXXXX-XXXXX</c> sobre un alfabeto sin ambigüedades, y quien los copia a
    /// mano o desde un fichero no debe fallar por el guion o por la caja.
    /// </summary>
    internal static string Normalizar(string codigo) =>
        new(codigo.Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());

    private async Task<List<string>> LeerHashesAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var valor = await GetTokenAsync(user, ProveedorInterno, NombreTokenCodigosRecuperacion, cancellationToken);
        if (string.IsNullOrEmpty(valor)) return [];

        return valor.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(h => h.StartsWith(PrefijoFormato, StringComparison.Ordinal))
            .ToList();
    }

    private Task GuardarHashesAsync(ApplicationUser user, IEnumerable<string> hashes, CancellationToken cancellationToken) =>
        SetTokenAsync(user, ProveedorInterno, NombreTokenCodigosRecuperacion, string.Join(";", hashes), cancellationToken);

    private static string CalcularHash(string codigo)
    {
        var sal = RandomNumberGenerator.GetBytes(BytesSal);
        var hash = Derivar(Normalizar(codigo), sal);
        return $"{PrefijoFormato}{Convert.ToBase64String(sal)}${Convert.ToBase64String(hash)}";
    }

    private static bool Coincide(string almacenado, string codigoNormalizado)
    {
        var partes = almacenado[PrefijoFormato.Length..].Split('$');
        if (partes.Length != 2) return false;

        byte[] sal, esperado;
        try
        {
            sal = Convert.FromBase64String(partes[0]);
            esperado = Convert.FromBase64String(partes[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Derivar(codigoNormalizado, sal), esperado);
    }

    private static byte[] Derivar(string codigoNormalizado, byte[] sal) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(codigoNormalizado), sal, Iteraciones, HashAlgorithmName.SHA256, BytesHash);
}
