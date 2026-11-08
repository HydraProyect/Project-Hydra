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
    /// True si el token trae algún permiso de aplicación de correo de Graph (<c>Mail.*</c>) concedido en Entra,
    /// o si no se puede leer: ante la duda, el token no se usa.
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

            foreach (var rol in roles.EnumerateArray())
            {
                if (rol.ValueKind != JsonValueKind.String ||
                    rol.GetString()!.StartsWith("Mail.", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
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
