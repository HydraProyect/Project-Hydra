namespace CaeManager.Domain.Plataforma;

/// <summary>
/// Qué puede hacer un usuario de la plataforma sobre un tenant ajeno —
/// ADR-011 § 8.2.
///
/// <b>Son capacidades, no un rol.</b> La matriz del plano 3 es
/// <i>capability-based</i> a propósito: nunca un
/// <c>PlatformAdmin</c> monolítico del que cuelguen implícitamente todas. Meter
/// "puede consultar documentos de cualquier tenant" dentro de
/// <see cref="AdminPlataforma"/> reintroduciría exactamente el problema que
/// este plano elimina — administrar la infraestructura y leer el contenido de
/// los clientes son dos permisos distintos que se conceden por separado.
/// </summary>
public enum CapacidadPrivilegio
{
    /// <summary>
    /// Inspección de solo lectura de un tenant. Sin escritura operativa, sin
    /// excepción implícita: la escritura excepcional es
    /// <see cref="BreakGlass"/>, y es otra concesión.
    /// </summary>
    SoporteLectura = 0,

    /// <summary>
    /// Reproducir la sesión de un usuario concreto. La autorización se evalúa
    /// con el contexto del simulado; la identidad del actor real se conserva
    /// siempre (ADR-011 § 8.4). Simular no amplía: quien impersona ve
    /// exactamente lo que vería esa persona, ni un dato más.
    /// </summary>
    Impersonacion = 1,

    /// <summary>
    /// Escritura excepcional para resolver un incidente. Exige motivo,
    /// duración, auditoría íntegra y revisión posterior. No se concede por
    /// defecto ni se deduce de ninguna otra capacidad.
    /// </summary>
    BreakGlass = 2,

    /// <summary>
    /// Gestión de tenants, facturación, configuración global, diagnóstico.
    /// <b>No implica leer el contenido documental de ningún tenant</b>: para
    /// eso hace falta <see cref="SoporteLectura"/>, concedida aparte.
    /// </summary>
    AdminPlataforma = 3
}
