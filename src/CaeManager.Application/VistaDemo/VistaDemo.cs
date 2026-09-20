namespace CaeManager.Application.VistaDemo;

/// <summary>
/// Las tres visiones de la plataforma que el selector de la cabecera ofrece a
/// una cuenta de demo. Es una LENTE PRESENTACIONAL, nunca una autoridad: solo
/// puede estrechar (o reetiquetar) lo que la cuenta ya tiene autorizado por su
/// rol real y sus Asignaciones de Cartera, jamás conceder una capacidad, un
/// alcance ni acceso a otro Tenant. <c>identidad ≠ contexto ≠ capacidad ≠
/// autorización ≠ alcance</c>: esta coordenada es de contexto, y una
/// coordenada de contexto no es autoridad.
///
/// "Dirección" es el rótulo de UI del nivel de Dirección del Operador CAE; su
/// nombre canónico definitivo es una decisión del propietario y aún no está
/// fijado (no confundir con el rol <c>DireccionCae</c>, que es un rol de
/// acceso del sistema).
/// </summary>
public enum VistaDemo
{
    /// <summary>Visión completa del Operador CAE: la autorización real de la cuenta, sin acotar.</summary>
    Direccion = 0,

    /// <summary>Coordinador CAE: la misma autorización, sin las vistas ejecutivas propias de la Dirección (solo menú).</summary>
    CoordinadorCae = 1,

    /// <summary>Gestor CAE: solo la Asignación de Cartera de un Gestor CAE concreto.</summary>
    GestorCae = 2,
}
