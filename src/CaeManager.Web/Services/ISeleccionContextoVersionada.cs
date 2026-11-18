namespace CaeManager.Web.Services;

/// <summary>
/// La parte de la selección de contexto que ve la guarda de pestaña obsoleta (D-6 b, R-2): con qué versión pintó este
/// ámbito, si se retiró después de pintar y a qué sesión de navegador pertenece. Solo la implementa la selección leída de
/// la cookie compartida; un contexto explícito (la extensión) o un doble de prueba no la implementan, y la guarda no los
/// rechaza. No es una frontera de autorización.
/// </summary>
internal interface ISeleccionContextoVersionada
{
    /// <summary>La versión de la selección con la que pinta este ámbito, o <c>null</c> si su contexto no sale de la cookie.</summary>
    string? VersionSeleccion { get; }

    /// <summary>La sesión de navegador (usuario y <c>talveg:sid</c>), o <c>null</c> sin usuario.</summary>
    string? ClaveSesion { get; }

    /// <summary>La selección se retiró después de que este ámbito la leyera para pintar.</summary>
    bool RetiradaTrasLeer { get; }

    /// <summary>Hace vigente en su sesión de navegador la selección con la que pinta este ámbito.</summary>
    void AdoptarComoVigente(RegistroSeleccionVigente registro);
}
