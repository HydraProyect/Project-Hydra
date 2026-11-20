using CaeManager.Domain.Common;

namespace CaeManager.Domain.Inbound.ControlAcceso;

/// <summary>
/// Un Centro en el que una persona con el rol «Control de acceso» (<c>Roles.ControlAcceso</c>) opera la portería del Tenant TCE
/// (AF-01 del plan de control de acceso físico, D-12). El rol por sí solo no abre ningún Centro: solo opera en los que el Gestor
/// CAE le asigna aquí, y en cualquier otro Centro del mismo Tenant recibe una lista vacía. Es una asignación operativa, no
/// propiedad ni privilegio de plataforma, y no concede ninguna capacidad de lectura de Inbound.
/// <para>
/// El usuario no lleva clave foránea (su cuenta vive en Identity, como en las demás asignaciones por usuario): lo comprueba el
/// comando al asignar. El Centro sí: FK compuesta con <c>TenantId</c>, así que un Centro de otro Tenant es irrepresentable.
/// Asignar es insertar y retirar es borrar; no hay UPDATE.
/// </para>
/// </summary>
public class UsuarioControlAccesoCentro : EntidadConTenant
{
    public Guid UsuarioId { get; private set; }

    public Guid CentroId { get; private set; }

    private UsuarioControlAccesoCentro()
    {
        // Requerido por EF Core.
    }

    public UsuarioControlAccesoCentro(Guid usuarioId, Guid centroId)
    {
        if (usuarioId == Guid.Empty) throw new ArgumentException("Falta el usuario.", nameof(usuarioId));
        if (centroId == Guid.Empty) throw new ArgumentException("Falta el Centro.", nameof(centroId));

        UsuarioId = usuarioId;
        CentroId = centroId;
    }
}
