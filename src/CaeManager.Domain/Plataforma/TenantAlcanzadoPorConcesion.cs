using CaeManager.Domain.Common;

namespace CaeManager.Domain.Plataforma;

/// <summary>
/// Un tenant dentro del alcance de una <see cref="ConcesionPrivilegio"/>
/// acotada. Es una tabla hija y no una columna con varios Ids porque el alcance
/// se consulta ("¿cubre esta concesión al tenant X?") y se audita, y una lista
/// serializada no se puede indexar ni leer sin interpretarla.
///
/// Catálogo global, igual que su concesión: un alcance cruza tenants por
/// definición.
/// </summary>
public class TenantAlcanzadoPorConcesion : Entity
{
    public Guid ConcesionPrivilegioId { get; private set; }
    public Guid TenantId { get; private set; }

    private TenantAlcanzadoPorConcesion()
    {
        // Requerido por EF Core.
    }

    public TenantAlcanzadoPorConcesion(Guid concesionPrivilegioId, Guid tenantId)
    {
        if (concesionPrivilegioId == Guid.Empty)
            throw new ArgumentException("El alcance debe pertenecer a una concesión.", nameof(concesionPrivilegioId));
        if (tenantId == Guid.Empty)
            throw new ArgumentException("El alcance debe apuntar a un tenant.", nameof(tenantId));

        ConcesionPrivilegioId = concesionPrivilegioId;
        TenantId = tenantId;
    }
}
