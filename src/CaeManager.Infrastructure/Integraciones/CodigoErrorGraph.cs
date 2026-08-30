using System.Text.Json;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Extrae solo el código de error de un cuerpo de respuesta de error de
/// Microsoft Graph/Entra ID (<c>{"error":{"code":"...","message":"..."}}</c>)
/// — nunca el mensaje completo ni el cuerpo crudo. El mensaje de error de un
/// proveedor externo no está bajo control de Hydra: puede repetir
/// direcciones de correo, identificadores u otros fragmentos de la petición
/// que lo provocó (auditoría módulo 6). Los logs de aplicación no son un
/// canal protegido ni de retención corta, así que solo el código —diseñado
/// por Microsoft para ser estable y no sensible— se considera seguro de
/// persistir ahí.
/// </summary>
internal static class CodigoErrorGraph
{
    public static string Extraer(string cuerpoJson)
    {
        try
        {
            using var documento = JsonDocument.Parse(cuerpoJson);
            if (documento.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("code", out var codigo) &&
                codigo.GetString() is { Length: > 0 } valor)
            {
                return valor;
            }
        }
        catch (JsonException)
        {
            // Cuerpo no es JSON (p. ej. HTML de un proxy/gateway intermedio)
            // — se informa como desconocido en vez de propagar el cuerpo crudo.
        }

        return "desconocido";
    }
}
