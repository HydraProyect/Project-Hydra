namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Roles del sistema, jerarquía de mayor a menor alcance: Administrador y
/// DireccionCae ven todo el negocio; CoordinadorCae ve la cartera de los
/// GestorCae que tiene asignados (ver AsignarCoordinadorCommand, futuro);
/// GestorCae ve solo sus propios Clientes (creados o asignados — ver
/// Cliente.EjecutivoUsuarioId); Consulta ve todo en solo lectura; Cliente
/// ve únicamente su propio Cliente (ApplicationUser.ClienteId) en solo
/// lectura. El código de cada constante es el nombre real guardado en
/// AspNetRoles — estable aunque el nombre visible cambie (ver
/// <see cref="NombreVisible"/>), para no invalidar asignaciones ya hechas.
/// CoordinadorCae y GestorCae se llamaban "Supervisor" y "EjecutivoCae" en
/// versiones anteriores — ver migración AgregarRolesJerarquia.
/// </summary>
public static class Roles
{
    public const string Administrador = "Administrador";
    public const string DireccionCae = "DireccionCae";
    public const string CoordinadorCae = "CoordinadorCae";
    public const string GestorCae = "GestorCae";
    public const string Consulta = "Consulta";
    public const string Cliente = "Cliente";

    public static readonly IReadOnlyList<string> Todos =
        [Administrador, DireccionCae, CoordinadorCae, GestorCae, Consulta, Cliente];

    private static readonly IReadOnlyDictionary<string, string> NombresVisibles = new Dictionary<string, string>
    {
        [Administrador] = "Administrador",
        [DireccionCae] = "Dirección CAE",
        [CoordinadorCae] = "Coordinador CAE",
        [GestorCae] = "Gestor CAE",
        [Consulta] = "Consulta",
        [Cliente] = "Cliente"
    };

    /// <summary>Etiqueta en español para mostrar en la UI — el código interno nunca se muestra tal cual.</summary>
    public static string NombreVisible(string codigo) => NombresVisibles.GetValueOrDefault(codigo, codigo);
}
