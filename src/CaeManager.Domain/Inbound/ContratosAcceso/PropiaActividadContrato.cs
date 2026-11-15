namespace CaeManager.Domain.Inbound.ContratosAcceso;

/// <summary>
/// Si la contrata es de la propia actividad del Cliente empresarial (R-7, DECISION_LOG 2026-11-14): sí, no o no lo sé.
/// Decide si los certificados de Seguridad Social y Hacienda son requisito legal o contractual; esa resolución entra con
/// los requisitos (B-15), no aquí. Se guarda como texto.
/// </summary>
public enum PropiaActividadContrato
{
    Si,
    No,
    NoLoSe
}
