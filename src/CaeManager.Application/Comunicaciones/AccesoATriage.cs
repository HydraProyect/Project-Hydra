namespace CaeManager.Application.Comunicaciones;

/// <summary>
/// Quién alcanza el contenido de un hilo sin ancla (la cola de triage), que no
/// tiene cartera que lo acote: solo los roles de gestión CAE de
/// <c>RolesComunicaciones.Gestion</c>. Esa constante vive en Web y aquí no se
/// alcanza; <c>RolesComunicacionesEnApplicationTests</c> vigila que no diverjan.
/// Lista cerrada: un rol nuevo queda fuera hasta que alguien lo decida.
/// </summary>
public static class AccesoATriage
{
    public static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.Ordinal)
    {
        "Administrador", "DireccionCae", "CoordinadorCae", "GestorCae", "Consulta"
    };

    public static bool EsTriage(Guid? clienteId, Guid? empresaId) => clienteId is null && empresaId is null;

    public static bool RolPermitido(string? rol) => rol is not null && Roles.Contains(rol);
}
