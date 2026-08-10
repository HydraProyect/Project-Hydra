namespace CaeManager.Domain.Subcontratas;

/// <summary>
/// Qué encontró el Gestor al comprobar un documento de la Subcontrata en la
/// plataforma del titular (ADR-005 § 2.3).
/// </summary>
public enum ResultadoVerificacionExterna
{
    /// <summary>El documento está en la plataforma y consta como válido.</summary>
    Valido = 0,

    /// <summary>El documento está pero la plataforma lo da por rechazado o caducado.</summary>
    NoValido = 1,

    /// <summary>El documento no aparece en la plataforma.</summary>
    NoEncontrado = 2
}
