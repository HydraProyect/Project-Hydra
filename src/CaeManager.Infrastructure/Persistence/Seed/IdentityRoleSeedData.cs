using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>Roles semilla con Id fijo para que la migración sea determinista.</summary>
public static class IdentityRoleSeedData
{
    private static readonly Guid AdministradorId = new("30000000-0000-0000-0000-000000000001");
    private static readonly Guid SupervisorId = new("30000000-0000-0000-0000-000000000002");
    private static readonly Guid EjecutivoCaeId = new("30000000-0000-0000-0000-000000000003");
    private static readonly Guid ConsultaId = new("30000000-0000-0000-0000-000000000004");

    public static IEnumerable<IdentityRole<Guid>> Filas() =>
    [
        Crear(AdministradorId, Roles.Administrador),
        Crear(SupervisorId, Roles.Supervisor),
        Crear(EjecutivoCaeId, Roles.EjecutivoCae),
        Crear(ConsultaId, Roles.Consulta)
    ];

    private static IdentityRole<Guid> Crear(Guid id, string nombre) => new(nombre)
    {
        Id = id,
        NormalizedName = nombre.ToUpperInvariant(),
        ConcurrencyStamp = id.ToString()
    };
}
