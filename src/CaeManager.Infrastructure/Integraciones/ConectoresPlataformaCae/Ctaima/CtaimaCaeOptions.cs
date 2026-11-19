namespace CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Ctaima;

/// <summary>
/// Dónde y cuánto esperar a la API oficial de CTAIMACAE.net. No lleva la clave de suscripción ni el
/// <c>tenantId</c>: la clave es la credencial de cada conexión y llega cifrada por la puerta, nunca por
/// configuración. Que el conector llame o no lo decide la puerta
/// (<c>OperacionesConectorPlataformaCae</c>): el interruptor del proveedor nace apagado.
/// </summary>
public sealed class CtaimaCaeOptions
{
    public const string SeccionConfiguracion = "Integraciones:CtaimaCae";

    /// <summary>Raíz común de las APIs del fabricante; cada operación cuelga su ruta relativa debajo.</summary>
    public string UrlBase { get; set; } = "https://ctaima.azure-api.net/api/ctaimacae/";

    /// <summary>Límite de una llamada completa. Sin reintentos: el alta no es idempotente del otro lado.</summary>
    public int TimeoutSegundos { get; set; } = 30;

    /// <summary>Solo HTTPS absoluto y terminado en barra, para que las rutas relativas se cuelguen debajo y no la sustituyan.</summary>
    public bool EsValida() =>
        Uri.TryCreate(UrlBase, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.AbsolutePath.EndsWith('/')
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.UserInfo)
        && TimeoutSegundos is > 0 and <= 120;
}
