namespace CaeManager.Domain.Cumplimiento;

/// <summary>
/// De dónde sale una <see cref="InstruccionTratamientoIaTenantPropietario"/> —
/// nunca "elegido libremente por el tenant" (DEC-33, REC-035): la instrucción
/// documentada la registra TALVEG como plataforma, a partir de una relación
/// contractual ya existente, no la activa el propio Tenant propietario desde
/// su panel.
/// </summary>
public enum OrigenInstruccionTratamientoIa
{
    /// <summary>
    /// Alta manual por un Administrador de la plataforma TALVEG (hoy: única
    /// vía real, mientras no exista <c>ProductoContratado</c> — ADR-011 § 2.6).
    /// Mismo criterio que <c>VincularSuscripcionStripe</c> (ADR-009 § 2.5):
    /// registro manual de un hecho contractual ya cerrado fuera del sistema.
    /// </summary>
    AltaManualPlataforma,

    /// <summary>
    /// Derivada automáticamente de la activación de un Producto Contratado
    /// (ADR-011 § 2.6) que cubra tratamiento con IA. No usado hasta que esa
    /// entidad exista — declarado aquí para que el día que se construya no
    /// haga falta abrir el enum, ver decisiones a elevar del RETURN PACKAGE
    /// de HO-035-02.
    /// </summary>
    ProductoContratado
}
