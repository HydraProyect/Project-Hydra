using CaeManager.Domain.Common;

namespace CaeManager.Domain.Configuracion;

/// <summary>
/// Estado por tenant de un trabajo automático del sistema (Configuración,
/// Parte XVI PROMPT 08 — "Automatizaciones"). Una fila por
/// (Tenant, TrabajoId); se crea perezosamente la primera vez que un hosted
/// service comprueba si está activo o registra una ejecución — no hace
/// falta sembrarla para cada tenant existente de antemano.
///
/// <see cref="TrabajoId"/> reutiliza la MISMA clave que ya usan los hosted
/// services para <c>IEleccionLiderService.IntentarEjecutarComoLiderAsync</c>
/// (p. ej. "ingesta-webhook-microsoft365") — es el identificador natural del
/// trabajo, no hace falta inventar uno nuevo.
/// </summary>
public class EstadoAutomatizacion : EntidadConTenant
{
    /// <summary>Mismo criterio que <see cref="Integraciones.ConexionIntegracion.LongitudMaximaUltimoError"/>: un mensaje de excepción no tiene límite y la columna sí.</summary>
    public const int LongitudMaximaUltimoMensajeError = 1000;

    public string TrabajoId { get; private set; } = string.Empty;
    public bool Activo { get; private set; } = true;
    public DateTime? UltimaEjecucionUtc { get; private set; }
    public bool? UltimoResultadoExitoso { get; private set; }

    /// <summary>Solo tiene valor cuando <see cref="UltimoResultadoExitoso"/> es false — REC-126: antes del panel solo mostraba "Fallida" sin decir por qué.</summary>
    public string? UltimoMensajeError { get; private set; }

    /// <summary>Cuántos elementos evaluó la última ejecución (p. ej. documentos y trabajadores mirados en un diagnóstico de retención). Null cuando el trabajo no reporta este dato.</summary>
    public int? UltimosElementosEvaluados { get; private set; }

    /// <summary>Cuántos elementos afectó realmente la última ejecución (p. ej. solicitudes de purga creadas). Null cuando el trabajo no reporta este dato.</summary>
    public int? UltimosElementosAfectados { get; private set; }

    private EstadoAutomatizacion()
    {
    }

    public EstadoAutomatizacion(string trabajoId)
    {
        if (string.IsNullOrWhiteSpace(trabajoId))
            throw new ArgumentException("El trabajo debe tener un identificador.", nameof(trabajoId));

        TrabajoId = trabajoId;
        Activo = true;
    }

    public void CambiarActivo(bool activo) => Activo = activo;

    public void RegistrarEjecucion(
        DateTime ejecutadaEnUtc, bool exitosa,
        string? mensajeError = null, int? elementosEvaluados = null, int? elementosAfectados = null)
    {
        UltimaEjecucionUtc = ejecutadaEnUtc;
        UltimoResultadoExitoso = exitosa;
        UltimoMensajeError = mensajeError is { Length: > LongitudMaximaUltimoMensajeError }
            ? mensajeError[..LongitudMaximaUltimoMensajeError]
            : mensajeError;
        UltimosElementosEvaluados = elementosEvaluados;
        UltimosElementosAfectados = elementosAfectados;
    }
}
