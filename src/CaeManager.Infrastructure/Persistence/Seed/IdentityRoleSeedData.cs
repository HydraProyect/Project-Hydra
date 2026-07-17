using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Roles semilla con Id fijo para que la migración sea determinista. Los Id
/// de Supervisor/EjecutivoCae se conservan (solo cambia su Name/
/// NormalizedName a CoordinadorCae/GestorCae) para que la migración sea un
/// UPDATE y no un borrado+alta — así ningún AspNetUserRoles existente
/// pierde su vínculo por RoleId.
/// </summary>
public static class IdentityRoleSeedData
{
    private static readonly Guid AdministradorId = new("30000000-0000-0000-0000-000000000001");
    private static readonly Guid CoordinadorCaeId = new("30000000-0000-0000-0000-000000000002");
    private static readonly Guid GestorCaeId = new("30000000-0000-0000-0000-000000000003");
    private static readonly Guid ConsultaId = new("30000000-0000-0000-0000-000000000004");
    private static readonly Guid DireccionCaeId = new("30000000-0000-0000-0000-000000000005");
    private static readonly Guid ClienteId = new("30000000-0000-0000-0000-000000000006");

    public static IEnumerable<IdentityRole<Guid>> Filas() =>
    [
        Crear(AdministradorId, Roles.Administrador),
        Crear(CoordinadorCaeId, Roles.CoordinadorCae),
        Crear(GestorCaeId, Roles.GestorCae),
        Crear(ConsultaId, Roles.Consulta),
        Crear(DireccionCaeId, Roles.DireccionCae),
        Crear(ClienteId, Roles.Cliente)
    ];

    private static IdentityRole<Guid> Crear(Guid id, string nombre) => new(nombre)
    {
        Id = id,
        NormalizedName = nombre.ToUpperInvariant(),
        ConcurrencyStamp = id.ToString()
    };
}
