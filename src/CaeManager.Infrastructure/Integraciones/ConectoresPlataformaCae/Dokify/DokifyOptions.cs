namespace CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Dokify;

/// <summary>
/// Dónde y cuánto esperar a la API pública de dokify. No lleva el token: es la credencial de cada Conexión
/// y llega cifrada por la puerta (<c>OperacionesConectorPlataformaCae</c>), nunca por configuración. Que el
/// conector llame o no lo decide la puerta: el interruptor del proveedor nace apagado.
/// </summary>
public sealed class DokifyOptions
{
    public const string SeccionConfiguracion = "Integraciones:Dokify";

    /// <summary>Raíz de la API pública; cada operación cuelga su ruta relativa debajo.</summary>
    public string UrlBase { get; set; } = "https://app.dokify.net/app/";

    /// <summary>Límite de una llamada completa. Sin reintentos: no hay límites de uso documentados.</summary>
    public int TimeoutSegundos { get; set; } = 30;

    // shortcut: la wiki muestra «Authorization: TOKEN» sin decir si lleva el esquema Bearer. Se parte de Bearer, lo
    // habitual en OAuth2, y vacío envía el token tal cual. Cuando el soporte de dokify (ticket 88270) responda, se
    // fija el valor y se retira la opción.
    public string EsquemaAutorizacion { get; set; } = "Bearer";

    /// <summary>Solo HTTPS absoluto y terminado en barra, para que las rutas relativas se cuelguen debajo y no la sustituyan.</summary>
    public bool EsValida() =>
        Uri.TryCreate(UrlBase, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.AbsolutePath.EndsWith('/')
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.UserInfo)
        && TimeoutSegundos is > 0 and <= 120
        && (EsquemaAutorizacion is null || (EsquemaAutorizacion.Length <= 16 && EsquemaAutorizacion.All(char.IsAsciiLetter)));
}
