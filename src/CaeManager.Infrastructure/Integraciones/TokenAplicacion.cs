using System.Text;
using System.Text.Json;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Lectura del claim <c>roles</c> de un token de aplicación de Microsoft Entra, sin validar la firma: el token
/// llega directamente de Entra por TLS en respuesta a nuestra petición, y aquí solo se mira qué permisos de
/// aplicación concedidos en Entra trae. Los roles de RBAC for Applications de Exchange no viajan en el token.
/// </summary>
internal static class TokenAplicacion
{
    /// <summary>
    /// True si el token trae algún permiso de aplicación concedido en Entra, o si no se puede leer: ante la duda,
    /// el token no se usa. Este flujo no necesita ninguno (el acceso a buzones lo da RBAC for Applications en
    /// Exchange), y una lista de denegación por nombre dejaría pasar permisos de buzón que no empiezan por
    /// <c>Mail.</c> (<c>MailboxItem.*</c>, <c>MailboxFolder.*</c>, los que Microsoft añada): se exige que no haya
    /// ninguno.
    /// </summary>
    public static bool TienePermisoDeCorreoSinAcotar(string accessToken)
    {
        var partes = accessToken.Split('.');
        if (partes.Length != 3)
            return true;

        try
        {
            using var carga = JsonDocument.Parse(DecodificarBase64Url(partes[1]));
            if (!carga.RootElement.TryGetProperty("roles", out var roles))
                return false;

            if (roles.ValueKind != JsonValueKind.Array)
                return true;

            return roles.GetArrayLength() > 0;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return true;
        }
    }

    private static string DecodificarBase64Url(string valor)
    {
        var base64 = valor.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }
}
