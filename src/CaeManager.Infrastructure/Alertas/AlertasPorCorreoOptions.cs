namespace CaeManager.Infrastructure.Alertas;

/// <summary>
/// Interruptor del resumen diario de alertas de vencimiento por correo
/// (Issue #2). Apagado por defecto — mismo criterio que
/// RetencionDatos:Activa/Comunicaciones:Activo: que <c>Graph:*</c> quede
/// configurado (para el email de bienvenida de Usuarios.razor, por ejemplo)
/// no debe activar por sí solo un envío periódico de correo real a usuarios
/// reales — es una decisión aparte.
/// </summary>
public class AlertasPorCorreoOptions
{
    public const string SeccionConfiguracion = "AlertasPorCorreo";

    public bool Activo { get; set; }

    /// <summary>
    /// URL pública de la aplicación (ej. https://caemanager.up.railway.app),
    /// para construir el enlace a /alertas dentro del correo. Sin
    /// configurar, el resumen se envía igual, solo que sin enlace clicable.
    /// </summary>
    public string? UrlBase { get; set; }
}
