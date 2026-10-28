namespace CaeManager.Application.Seguridad.Incidentes;

/// <summary>
/// Umbrales de las señales de incidente de seguridad y canal de alerta a la guardia de
/// TALVEG (sección <c>IncidentesSeguridad</c>). Los umbrales por defecto son la propuesta
/// inicial del incremento 1, ajustables por configuración sin desplegar código.
/// <see cref="Validar"/> se comprueba al arrancar: un umbral no positivo apagaría la
/// señal en silencio y una URL de alerta sin https expondría su token.
/// </summary>
public sealed class OpcionesIncidentesSeguridad
{
    public const string Seccion = "IncidentesSeguridad";

    /// <summary>Errores de configuración; vacío si es válida.</summary>
    public IReadOnlyList<string> Validar()
    {
        var errores = new List<string>();
        if (UmbralFilasExportadas <= 0)
            errores.Add($"{Seccion}:{nameof(UmbralFilasExportadas)} tiene que ser positivo.");
        if (UmbralDocumentosSensibles <= 0)
            errores.Add($"{Seccion}:{nameof(UmbralDocumentosSensibles)} tiene que ser positivo.");
        if (UmbralRechazosOtroTenant <= 0)
            errores.Add($"{Seccion}:{nameof(UmbralRechazosOtroTenant)} tiene que ser positivo.");
        if (!string.IsNullOrWhiteSpace(UrlAlertaSaliente)
            && !(Uri.TryCreate(UrlAlertaSaliente, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps))
            errores.Add($"{Seccion}:{nameof(UrlAlertaSaliente)} tiene que ser una URL absoluta https.");
        return errores;
    }

    /// <summary>
    /// Ventana de deduplicación: la misma señal sobre el mismo sujeto dentro de ella
    /// suma una detección al incidente abierto en vez de crear otro. La función de
    /// registro la acota a [1 minuto, 24 horas].
    /// </summary>
    public int MinutosVentanaDeduplicacion { get; set; } = 60;

    /// <summary>Filas exportadas (Excel) por un mismo usuario dentro de la ventana que hacen saltar la extracción masiva.</summary>
    public int UmbralFilasExportadas { get; set; } = 5_000;

    /// <summary>Aperturas o descargas de documentos sensibles por un mismo usuario dentro de la ventana que hacen saltar la extracción masiva.</summary>
    public int UmbralDocumentosSensibles { get; set; } = 300;

    /// <summary>Ventana deslizante de la extracción masiva.</summary>
    public int MinutosVentanaExtraccion { get; set; } = 60;

    /// <summary>Rechazos de autorización al pedir abrir otro Tenant, de un mismo usuario, dentro de la ventana.</summary>
    public int UmbralRechazosOtroTenant { get; set; } = 5;

    /// <summary>Ventana deslizante de los rechazos al pedir otro Tenant.</summary>
    public int MinutosVentanaRechazosOtroTenant { get; set; } = 10;

    /// <summary>
    /// URL del canal de alerta (heartbeat o incidente de Better Stack). Vacía = canal
    /// desactivado: el incidente se registra igual y el log avisa de que no salió alerta.
    /// Contiene un token: va en secretos o variables de entorno, nunca en appsettings.
    /// </summary>
    public string? UrlAlertaSaliente { get; set; }

    /// <summary>Origen público de la aplicación para el enlace de la alerta (p. ej. <c>https://app.example</c>). Vacío = enlace relativo.</summary>
    public string? UrlBasePlataforma { get; set; }

    public TimeSpan VentanaDeduplicacion => TimeSpan.FromMinutes(Math.Clamp(MinutosVentanaDeduplicacion, 1, 24 * 60));

    public TimeSpan VentanaExtraccion => TimeSpan.FromMinutes(Math.Max(1, MinutosVentanaExtraccion));

    public TimeSpan VentanaRechazosOtroTenant => TimeSpan.FromMinutes(Math.Max(1, MinutosVentanaRechazosOtroTenant));

    /// <summary>Ruta de la página de confirmación (incremento 3) para un incidente.</summary>
    public static string RutaIncidente(Guid incidenteId) => $"/plataforma/incidentes/{incidenteId:D}";

    public string EnlaceIncidente(Guid incidenteId)
        => string.IsNullOrWhiteSpace(UrlBasePlataforma)
            ? RutaIncidente(incidenteId)
            : UrlBasePlataforma.TrimEnd('/') + RutaIncidente(incidenteId);
}
