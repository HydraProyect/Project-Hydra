using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CaeManager.Infrastructure.Persistence.ContextoRls;

/// <summary>
/// Coordenadas de sesión que la RLS consulta, antes de firmarlas. Son las
/// mismas tres que <c>TenantRlsConnectionInterceptor</c> fijaba sueltas en
/// <c>app.tenant_id</c>, <c>app.tenant_origen_id</c> y <c>app.usuario_id</c>,
/// más el <see cref="Origen"/> (informativo: quién abrió la conexión).
/// </summary>
public sealed record ContextoSesionRls(Guid? TenantId, Guid? TenantOrigenId, Guid? UsuarioId, string Origen);

/// <summary>Valores de <see cref="ContextoSesionRls.Origen"/>. Ninguno contiene '|' ni '.'.</summary>
public static class OrigenContextoRls
{
    /// <summary>Petición con usuario autenticado.</summary>
    public const string Peticion = "peticion";

    /// <summary>Sin usuario, dentro de un <c>AmbitoTenantExplicito</c> (trabajos de fondo, seeders).</summary>
    public const string Ambito = "ambito";

    /// <summary>Sin usuario ni ámbito (login, arranque).</summary>
    public const string Anonimo = "anonimo";

    /// <summary>
    /// <c>TenantSelladoInterceptor</c> cambió el Tenant a mitad de
    /// <c>SaveChanges</c> para sellar una fila de auditoría de Identity.
    /// </summary>
    public const string Sellado = "sellado";
}

/// <summary>
/// Formato y firma del token de contexto RLS (P6, diseño
/// <c>tecnico/DISENO-CONTEXTO-RLS-FIRMADO-P6-2026-09-23.md</c> § 3):
/// <code>v1|clave|tenant|tenant_origen|usuario|origen|pid|caduca_epoch|nonce.hmac_hex</code>
///
/// <para>
/// La validación vive en PostgreSQL (<c>app_contexto_validado()</c>, migración
/// <c>ContextoRlsFirmado</c>), que recalcula el HMAC-SHA256 con
/// <c>sha256(opad || sha256(ipad || carga))</c> — RFC 2104 con la clave ya
/// combinada con los rellenos, así que no hace falta <c>pgcrypto</c>. Por eso
/// la tabla de claves guarda <see cref="Rellenos"/> y no la clave: es el mismo
/// secreto (quien los tenga puede firmar), en la forma que la base sabe usar.
/// </para>
/// </summary>
public static class TokenContextoRls
{
    public const string Version = "v1";
    private const int TamanoBloqueSha256 = 64;

    /// <summary>
    /// <c>(K ⊕ ipad, K ⊕ opad)</c> de RFC 2104 para una clave de hasta 64 bytes
    /// (se rellena con ceros hasta el tamaño de bloque de SHA-256).
    /// </summary>
    public static (byte[] Ipad, byte[] Opad) Rellenos(ReadOnlySpan<byte> clave)
    {
        if (clave.Length is 0 or > TamanoBloqueSha256)
            throw new ArgumentException("La clave debe tener entre 1 y 64 bytes.", nameof(clave));

        var ipad = new byte[TamanoBloqueSha256];
        var opad = new byte[TamanoBloqueSha256];
        for (var i = 0; i < TamanoBloqueSha256; i++)
        {
            var k = i < clave.Length ? clave[i] : (byte)0;
            ipad[i] = (byte)(k ^ 0x36);
            opad[i] = (byte)(k ^ 0x5c);
        }
        return (ipad, opad);
    }

    public static string Construir(
        Guid claveId, ReadOnlySpan<byte> clave, ContextoSesionRls contexto, int pidBackend,
        DateTimeOffset caduca, string nonce)
    {
        if (contexto.Origen.Contains('|') || contexto.Origen.Contains('.'))
            throw new ArgumentException("El origen no puede contener '|' ni '.'.", nameof(contexto));
        if (nonce.Contains('|') || nonce.Contains('.'))
            throw new ArgumentException("El nonce no puede contener '|' ni '.'.", nameof(nonce));

        var carga = string.Join('|',
            Version,
            claveId.ToString("D"),
            Texto(contexto.TenantId),
            Texto(contexto.TenantOrigenId),
            Texto(contexto.UsuarioId),
            contexto.Origen,
            pidBackend.ToString(CultureInfo.InvariantCulture),
            caduca.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            nonce);

        var firma = HMACSHA256.HashData(clave, Encoding.UTF8.GetBytes(carga));
        return carga + "." + Convert.ToHexStringLower(firma);
    }

    private static string Texto(Guid? valor) => valor?.ToString("D") ?? string.Empty;
}
