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
    public string TrabajoId { get; private set; } = string.Empty;
    public bool Activo { get; private set; } = true;
    public DateTime? UltimaEjecucionUtc { get; private set; }
    public bool? UltimoResultadoExitoso { get; private set; }

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

    public void RegistrarEjecucion(DateTime ejecutadaEnUtc, bool exitosa)
    {
        UltimaEjecucionUtc = ejecutadaEnUtc;
        UltimoResultadoExitoso = exitosa;
    }
}
