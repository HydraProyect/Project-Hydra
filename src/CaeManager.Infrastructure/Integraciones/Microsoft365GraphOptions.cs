namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// App Registration de Entra ID para el conector de Microsoft 365 (P3-33 de
/// docs/business/MATURITY_REVIEW.md — buzón de correo conectado por
/// Cliente/Tenant, ver ARQUITECTURA-INTEGRACIONES.md § 12). A diferencia de
/// <c>AzureAdOptions</c>/<c>GraphEmailOptions</c> (single-tenant, atados a
/// la propia organización de Hydra), este App Registration debe ser
/// **multi-tenant** ("cuentas de cualquier organización") porque cada buzón
/// conectado pertenece al Entra ID de un cliente distinto — por eso el
/// endpoint de autorización usa <c>common</c>, no un <c>TenantId</c> fijo.
/// Permisos delegados necesarios: <c>Mail.Read</c>, <c>Mail.Send</c>,
/// <c>offline_access</c> (para el refresh token) — ver DEPLOY.md.
///
/// Apagado por defecto, mismo patrón que el resto de integraciones
/// opcionales: sin credenciales, la pantalla de Conexiones sigue mostrando
/// el botón "Conectar" pero el flujo falla con un aviso explícito en vez de
/// arrancar el proceso de arranque roto.
/// </summary>
public class Microsoft365GraphOptions
{
    public const string SeccionConfiguracion = "Integraciones:Microsoft365";

    public string? ClientId { get; set; }

    /// <summary>
    /// Secreto de cliente del App Registration. Sigue admitido como transición,
    /// pero el destino decidido por el propietario (P44b, 2026-09-19) es el
    /// <b>certificado</b>: un secreto de cliente es una credencial permanente y
    /// transportable como texto; con certificado, la clave privada no sale del
    /// servidor y a Entra solo se le sube la parte pública.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Ruta (dentro del contenedor) del certificado en PEM — solo la parte pública.
    /// Junto con <see cref="ClavePrivadaRuta"/> sustituye a <see cref="ClientSecret"/>:
    /// si ambas están informadas, el canje de tokens se autentica con un aserto de
    /// cliente firmado (RFC 7523) y <c>client_secret</c> no se envía.
    /// </summary>
    public string? CertificadoRuta { get; set; }

    /// <summary>Ruta (dentro del contenedor) de la clave privada RSA del certificado, en PEM.</summary>
    public string? ClavePrivadaRuta { get; set; }

    /// <summary>
    /// URL pública canónica de la aplicación (ej. https://app.talveg.es),
    /// sin barra final. El redirect_uri de OAuth y la URL de notificación de
    /// la suscripción de Graph se construyen SIEMPRE desde aquí, nunca desde
    /// <c>HttpRequest.Scheme</c>/<c>Host</c> (auditoría módulo 6): una
    /// configuración de proxy/host filtering incorrecta permitiría derivar
    /// esas URLs de seguridad desde una cabecera Host manipulada por quien
    /// hace la petición.
    /// </summary>
    public string? UrlPublicaBase { get; set; }

    /// <summary>Las dos rutas del certificado están informadas: se autentica con certificado.</summary>
    public bool UsaCertificado =>
        !string.IsNullOrWhiteSpace(CertificadoRuta) && !string.IsNullOrWhiteSpace(ClavePrivadaRuta);

    /// <summary>
    /// Solo una de las dos rutas está informada. Es una configuración a medias, y
    /// se trata como error (la integración queda apagada) en vez de caer en
    /// silencio a <see cref="ClientSecret"/>: quien empezó la migración a
    /// certificado y olvidó una ruta no debe seguir usando el secreto sin saberlo.
    /// </summary>
    public bool CertificadoIncompleto =>
        string.IsNullOrWhiteSpace(CertificadoRuta) != string.IsNullOrWhiteSpace(ClavePrivadaRuta);

    public bool EstaConfigurado =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(UrlPublicaBase)
        && !CertificadoIncompleto && (UsaCertificado || !string.IsNullOrWhiteSpace(ClientSecret));

    /// <summary>
    /// Qué falta cuando alguien ha empezado a configurar el conector y no lo ha
    /// terminado. Vacío si está configurado del todo o si no se ha informado
    /// NADA (apagado por defecto: eso es normal y no merece un aviso). Solo
    /// nombra opciones, nunca sus valores ni las rutas del certificado.
    /// Existe porque <see cref="EstaConfigurado"/> a <c>false</c> deja de
    /// registrar la ingesta del webhook y la renovación de la suscripción sin
    /// ningún rastro: las suscripciones de Graph ya creadas caducan a los ~3
    /// días sin renovarse y el correo de los buzones conectados deja de entrar.
    /// </summary>
    public IReadOnlyList<string> ProblemasDeConfiguracion()
    {
        if (EstaConfigurado) return [];

        var hayAlgoInformado = new[] { ClientId, ClientSecret, CertificadoRuta, ClavePrivadaRuta, UrlPublicaBase }
            .Any(valor => !string.IsNullOrWhiteSpace(valor));
        if (!hayAlgoInformado) return [];

        var problemas = new List<string>();
        if (string.IsNullOrWhiteSpace(ClientId)) problemas.Add("falta ClientId");
        if (string.IsNullOrWhiteSpace(UrlPublicaBase)) problemas.Add("falta UrlPublicaBase");

        if (CertificadoIncompleto)
        {
            problemas.Add(string.IsNullOrWhiteSpace(ClavePrivadaRuta)
                ? "CertificadoRuta está informada pero falta ClavePrivadaRuta"
                : "ClavePrivadaRuta está informada pero falta CertificadoRuta");
        }
        else if (!UsaCertificado && string.IsNullOrWhiteSpace(ClientSecret))
        {
            problemas.Add("falta una credencial: ni el certificado (CertificadoRuta y ClavePrivadaRuta) ni ClientSecret");
        }

        return problemas;
    }
}
