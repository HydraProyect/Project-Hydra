namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Roles semilla, alineados 1:1 con los cuatro dashboards del brief
/// (ver ARCHITECTURE.md, "Autenticación y autorización").
/// </summary>
public static class Roles
{
    public const string Administrador = "Administrador";
    public const string Supervisor = "Supervisor";
    public const string EjecutivoCae = "EjecutivoCae";
    public const string Consulta = "Consulta";

    public static readonly IReadOnlyList<string> Todos = [Administrador, Supervisor, EjecutivoCae, Consulta];
}
