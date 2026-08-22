namespace CaeManager.Domain.Plataforma;

/// <summary>
/// Por qué una concesión tiene la semántica que tiene — no cómo se ejecutó el
/// comando que la creó.
///
/// <para>
/// La distinción importa: "auto-concedida" describiría el mecanismo, y el
/// mecanismo no es una propiedad de la autoridad. Lo que este enum separa es la
/// concesión <b>fundacional</b> de todas las demás, para que identificarla no
/// dependa de una coincidencia de forma.
/// </para>
///
/// <para>
/// Sin esto habría que reconocer la raíz por su aspecto —<c>AdminPlataforma</c>
/// global— y eso <b>no discrimina</b>: <c>ConcesionPrivilegio.Global</c> obliga a
/// esa capacidad, así que toda concesión global futura tendría exactamente la
/// misma forma y pasaría por fundacional.
/// </para>
/// </summary>
public enum OrigenConcesion
{
    /// <summary>Lo normal: una concesión más, sin semántica especial.</summary>
    Ordinaria = 0,

    /// <summary>
    /// La concesión fundacional de la plataforma, la que existe antes de que
    /// exista ninguna autoridad de la que derivarla. Solo puede nacer de la ruta
    /// de bootstrap, solo sobre <see cref="CapacidadPrivilegio.AdminPlataforma"/>
    /// global, solo a nombre de la identidad raíz designada por el despliegue y
    /// solo una vez.
    /// </summary>
    BootstrapPlataforma = 1,
}
