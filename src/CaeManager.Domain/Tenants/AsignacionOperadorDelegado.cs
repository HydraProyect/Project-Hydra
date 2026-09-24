using CaeManager.Domain.Common;

namespace CaeManager.Domain.Tenants;

/// <summary>
/// Un Operador Delegado (usuario de la Consultora) autorizado a operar sobre
/// un <see cref="DelegacionTenant"/> concreto, con un rol específico para ese
/// Delegated Workspace — ver ADR-004-delegacion-consultoras-cae.md § 5.3. Un
/// mismo usuario puede tener roles distintos en delegaciones distintas (p.
/// ej. GestorCae en un cliente, Consulta en otro).
///
/// Extiende <see cref="Entity"/>, no <see cref="EntidadConTenant"/>: mismo
/// catálogo global que <see cref="DelegacionTenant"/>.
///
/// <see cref="UsuarioId"/> es un Guid suelto sin navegación EF hacia
/// <c>ApplicationUser</c> — Identity vive en Infrastructure, no accesible
/// desde Domain (mismo patrón que <c>ApplicationUser.CoordinadorUsuarioId</c>).
/// <see cref="Rol"/> es un código de rol en texto plano por el mismo motivo:
/// Application/Domain no referencian <c>CaeManager.Infrastructure.Identity.Roles</c>
/// para no invertir la dependencia entre capas (ver
/// AutorizacionEscrituraBehavior) — la validación de que sea un código de rol
/// conocido vive en el validador del Command, no aquí.
///
/// <b>Revocación (decisión del propietario 2026-09-23, P8).</b> Una asignación
/// revocada se conserva como historial y nunca se borra, igual que
/// <see cref="DelegacionTenant.Desactivar"/> desactiva en vez de borrar. No
/// hay operación inversa: revocar es definitivo, y para volver a operar se
/// crea una asignación nueva con un rol delegable. Los lectores no la ven: el
/// DbContext expone la tabla filtrada a las no revocadas, y el índice único
/// (delegación, usuario) solo cuenta las no revocadas, para que la revocada no
/// impida esa asignación nueva.
/// </summary>
public class AsignacionOperadorDelegado : Entity
{
    /// <summary>Longitud máxima del motivo de revocación en base de datos.</summary>
    public const int LongitudMaximaMotivoRevocacion = 200;

    public Guid DelegacionTenantId { get; private set; }
    public Guid UsuarioId { get; private set; }
    public string Rol { get; private set; } = string.Empty;
    public DateTime CreadoEnUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Cuándo se revocó. <c>null</c> mientras la asignación concede su rol.</summary>
    public DateTime? RevocadaEnUtc { get; private set; }

    /// <summary>Por qué se revocó. Obligatorio al revocar, igual que el motivo de cierre de una cartera.</summary>
    public string? MotivoRevocacion { get; private set; }

    public bool EstaRevocada => RevocadaEnUtc is not null;

    private AsignacionOperadorDelegado()
    {
        // Requerido por EF Core.
    }

    public AsignacionOperadorDelegado(Guid delegacionTenantId, Guid usuarioId, string rol)
    {
        if (delegacionTenantId == Guid.Empty)
            throw new ArgumentException("La asignación debe pertenecer a una delegación.", nameof(delegacionTenantId));
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("La asignación debe tener un Operador Delegado.", nameof(usuarioId));
        if (string.IsNullOrWhiteSpace(rol))
            throw new ArgumentException("La asignación debe tener un rol.", nameof(rol));

        DelegacionTenantId = delegacionTenantId;
        UsuarioId = usuarioId;
        Rol = rol.Trim();
    }

    /// <summary>
    /// Retira el rol sin borrar la fila. Definitivo: no existe reactivación.
    /// </summary>
    public void Revocar(string motivo, DateTime ahoraUtc)
    {
        if (EstaRevocada)
            throw new InvalidOperationException("La asignación ya estaba revocada.");
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("Revocar exige un motivo.", nameof(motivo));

        var motivoLimpio = motivo.Trim();
        if (motivoLimpio.Length > LongitudMaximaMotivoRevocacion)
            throw new ArgumentException(
                $"El motivo no puede superar {LongitudMaximaMotivoRevocacion} caracteres.", nameof(motivo));

        RevocadaEnUtc = ahoraUtc;
        MotivoRevocacion = motivoLimpio;
    }
}
