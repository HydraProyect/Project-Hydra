namespace CaeManager.Domain.Subcontratas;

/// <summary>
/// Nivel de servicio contratado sobre una Subcontrata (ADR-005, aprobado
/// 2026-08-10). No es un tipo de entidad: pasar de un nivel a otro es una
/// operación de negocio (<see cref="Subcontrata.CambiarNivelServicio"/>) que
/// conserva todo el historial.
/// </summary>
public enum NivelServicioSubcontrata
{
    /// <summary>
    /// Se sube y valida su documentación dentro de Hydra — la semántica que
    /// el producto tenía antes de existir esta distinción; por eso es el
    /// valor por defecto y el de todas las filas anteriores a ADR-005.
    /// </summary>
    Gestionada = 0,

    /// <summary>
    /// No se cargan ni validan sus documentos: solo se audita su cumplimiento
    /// en las plataformas del titular mediante
    /// <see cref="VerificacionExternaSubcontrata"/>.
    /// </summary>
    Supervisada = 1
}
