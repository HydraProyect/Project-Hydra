using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Proveedor del autenticador (TOTP de RFC 6238, SHA-1, 30 s, 6 cifras) en el que <b>un código ya
/// aceptado no vuelve a valer</b> (ASVS 4.0.3 V2.8.4). Sustituye al
/// <see cref="AuthenticatorTokenProvider{TUser}"/> de serie bajo el mismo nombre
/// (<see cref="TokenOptions.DefaultAuthenticatorProvider"/>), así que lo usan todas las rutas que
/// verifican el TOTP: el segundo paso del inicio de sesión
/// (<c>SignInManager.TwoFactorAuthenticatorSignInAsync</c>), la verificación del alta en
/// <c>ConfigurarAutenticadorDosFactores</c> y la reautenticación para regenerar los códigos de
/// recuperación (<see cref="SegundoFactorDeCuentasIdentity.GenerarCodigosRecuperacionAsync"/>).
///
/// <para>
/// <b>Regla.</b> Acepta la misma ventana que el proveedor de serie (el paso actual y dos a cada lado),
/// pero solo un paso <b>posterior</b> al último aceptado para la cuenta. Ese paso se guarda en
/// <c>AspNetUserTokens</c> (proveedor <see cref="AlmacenUsuarios.ProveedorInterno"/>, nombre
/// <see cref="NombreTokenUltimoPaso"/>), la misma tabla que la clave TOTP y los códigos de
/// recuperación: lo comparten todas las réplicas y no necesita migración. La escritura pasa por
/// <c>UserManager</c>, con la concurrencia optimista del <c>ConcurrencyStamp</c> de la cuenta: de dos
/// verificaciones simultáneas del mismo código solo una guarda, y la otra se rechaza.
/// </para>
///
/// <para>
/// <b>Huella de la clave.</b> El valor guardado lleva una huella de la clave con la que se aceptó el
/// paso (HMAC de la propia clave, truncado: no revela nada de ella). Con una clave nueva (reinicio del
/// autenticador, restablecimiento por un Administrador o por Soporte TALVEG) el paso anterior no
/// cuenta, y el primer código de la clave nueva vale aunque caiga en el mismo paso.
/// </para>
///
/// <para>
/// <b>Una misma acción, dos comprobaciones.</b> El alta verifica el código y, en la misma petición,
/// genera los códigos de recuperación con ese mismo código, que se vuelve a verificar. Esta instancia
/// (transitoria, del <c>UserManager</c> del ámbito de la petición) recuerda el paso que acaba de
/// aceptar y lo da por bueno otra vez dentro de su ámbito; cualquier otra petición lo rechaza.
/// </para>
/// </summary>
public class ProveedorTotpDeUnSoloUso(TimeProvider reloj) : AuthenticatorTokenProvider<ApplicationUser>
{
    /// <summary>Nombre del token de <c>AspNetUserTokens</c> que guarda el último paso aceptado.</summary>
    public const string NombreTokenUltimoPaso = "UltimoPasoTotp";

    private const int SegundosPorPaso = 30;
    private const int PasosDeTolerancia = 2;
    private const int CifrasDelCodigo = 6;
    private static readonly byte[] EtiquetaHuella = Encoding.ASCII.GetBytes("talveg:ultimo-paso-totp");

    private (Guid Usuario, string Huella, long Paso)? _aceptadoEnEsteAmbito;

    public override async Task<bool> ValidateAsync(
        string purpose, string token, UserManager<ApplicationUser> manager, ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(user);

        var clave = await manager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrWhiteSpace(clave) || !EsCodigoValido(token)
            || DescodificarBase32(clave) is not { Length: > 0 } bytesClave)
            return false;

        var huella = Huella(bytesClave);
        var pasoActual = reloj.GetUtcNow().ToUnixTimeSeconds() / SegundosPorPaso;

        if (_aceptadoEnEsteAmbito is { } previo && previo.Usuario == user.Id && previo.Huella == huella
            && Math.Abs(previo.Paso - pasoActual) <= PasosDeTolerancia
            && CoincideCodigo(bytesClave, previo.Paso, token))
            return true;

        var ultimo = UltimoPasoAceptado(
            await manager.GetAuthenticationTokenAsync(user, AlmacenUsuarios.ProveedorInterno, NombreTokenUltimoPaso),
            huella);

        long? aceptado = null;
        for (var paso = pasoActual - PasosDeTolerancia; paso <= pasoActual + PasosDeTolerancia; paso++)
        {
            if (paso > ultimo && CoincideCodigo(bytesClave, paso, token))
            {
                aceptado = paso;
                break;
            }
        }

        if (aceptado is not { } pasoAceptado)
            return false;

        IdentityResult guardado;
        try
        {
            guardado = await manager.SetAuthenticationTokenAsync(
                user, AlmacenUsuarios.ProveedorInterno, NombreTokenUltimoPaso,
                pasoAceptado.ToString(CultureInfo.InvariantCulture) + ":" + huella);
        }
        catch (DbUpdateException)
        {
            // Otra verificación de la misma cuenta guardó antes (carrera en el alta de la fila):
            // falla en cerrado, como la pérdida de concurrencia optimista de abajo.
            return false;
        }

        if (!guardado.Succeeded)
            return false;

        _aceptadoEnEsteAmbito = (user.Id, huella, pasoAceptado);
        return true;
    }

    /// <summary>
    /// Código TOTP del <paramref name="paso"/> para la clave dada, con el mismo cálculo que el
    /// proveedor de serie de Identity (RFC 6238 sobre HMAC-SHA1, truncado dinámico de RFC 4226).
    /// </summary>
    public static string CalcularCodigo(byte[] clave, long paso)
    {
        Span<byte> contador = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(contador, paso);
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(clave, contador, hash);
        var desplazamiento = hash[^1] & 0x0F;
        var binario = ((hash[desplazamiento] & 0x7F) << 24) | (hash[desplazamiento + 1] << 16)
            | (hash[desplazamiento + 2] << 8) | hash[desplazamiento + 3];
        return (binario % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>Clave del autenticador en Base32 (RFC 4648, sin relleno) a bytes; nulo si no es Base32.</summary>
    public static byte[]? DescodificarBase32(string base32)
    {
        const string Alfabeto = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var texto = base32.Trim().TrimEnd('=').ToUpperInvariant();
        var bytes = new byte[texto.Length * 5 / 8];
        int acumulado = 0, bits = 0, indice = 0;
        foreach (var caracter in texto)
        {
            var valor = Alfabeto.IndexOf(caracter, StringComparison.Ordinal);
            if (valor < 0) return null;
            acumulado = (acumulado << 5) | valor;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes[indice++] = (byte)(acumulado >> bits);
                acumulado &= (1 << bits) - 1;
            }
        }
        return bytes;
    }

    private static bool CoincideCodigo(byte[] clave, long paso, string token) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(CalcularCodigo(clave, paso)), Encoding.ASCII.GetBytes(token));

    private static bool EsCodigoValido(string? token) =>
        token is { Length: CifrasDelCodigo } && token.All(char.IsAsciiDigit);

    private static string Huella(byte[] clave) =>
        Convert.ToHexString(HMACSHA256.HashData(clave, EtiquetaHuella).AsSpan(0, 8));

    /// <summary>
    /// El último paso aceptado con esta misma clave; <see cref="long.MinValue"/> si no hay ninguno, si se
    /// aceptó con otra clave o si el valor guardado no se entiende (nada que proteger con él).
    /// </summary>
    private static long UltimoPasoAceptado(string? guardado, string huella)
    {
        if (guardado?.Split(':') is not [var paso, var huellaGuardada]
            || !string.Equals(huellaGuardada, huella, StringComparison.Ordinal)
            || !long.TryParse(paso, NumberStyles.None, CultureInfo.InvariantCulture, out var valor))
            return long.MinValue;
        return valor;
    }
}
