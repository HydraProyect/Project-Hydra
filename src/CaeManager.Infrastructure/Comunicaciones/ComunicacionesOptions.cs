namespace CaeManager.Infrastructure.Comunicaciones;

/// <summary>
/// Interruptor del módulo Comunicaciones completo (P2 #26 de
/// docs/business/MATURITY_REVIEW.md).
///
/// Se congeló el 2026-08-01 porque no había ingesta real de Microsoft Graph
/// detrás de la bandeja — el comité de revisión lo describió como "una
/// maqueta funcional cara". Esa razón ya no aplica: las Fases 72-78
/// (2026-08-02/03) construyeron la ingesta real completa (OAuth delegado,
/// webhooks entrantes, envío real vía /reply y /sendMail — ver
/// ARQUITECTURA-INTEGRACIONES.md § 12.6), así que el 2026-08-03 se decidió
/// reactivar el módulo por defecto.
///
/// Sigue pendiente de entorno, no de código: activarlo de verdad para un
/// tenant concreto exige un App Registration de Entra ID con permisos de
/// aplicación y consentimiento de administrador (ver
/// RUNBOOK-GRAPH-M365.md) — sin esas credenciales configuradas, la bandeja
/// se ve pero no hay ningún buzón conectado todavía. Activo por defecto no
/// es "ya funciona en producción sin hacer nada más", es "deja de
/// esconderse tras un flag que ya no protege nada real".
/// </summary>
public class ComunicacionesOptions
{
    public const string SeccionConfiguracion = "Comunicaciones";

    public bool Activo { get; set; } = true;
}
