using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;

namespace CaeManager.Domain.Tenants;

/// <summary>
/// Concesión de una <see cref="CapacidadTenant"/> a una cuenta del Tenant propietario (decisión D-3 del
/// 2026-11-15): la concede y la revoca el Administrador del Tenant propietario, y el beneficiario es siempre una
/// cuenta cuyo Tenant de origen es ese mismo Tenant. Quién puede conceder y a quién lo comprueba
/// <c>AutoridadCapacidadesTenant</c> en Application; aquí solo viven los invariantes de la fila.
///
/// <para>
/// <b>Historial, no ficha.</b> Revocar no borra: deja quién y cuándo, y una concesión posterior a la misma cuenta
/// es una fila nueva. Como mucho hay una concesión vigente por cuenta y capacidad (índice único parcial
/// <c>IX_ConcesionesCapacidadTenant_UnaVigentePorUsuarioYCapacidad</c>).
/// </para>
/// </summary>
public class ConcesionCapacidadTenant : EntidadConTenant, IVersionable, IAccionAuditoriaPropia
{
    /// <summary>La cuenta que recibe la capacidad.</summary>
    public Guid UsuarioId { get; private set; }

    public CapacidadTenant Capacidad { get; private set; }

    /// <summary>Actor real que la concedió: el Administrador del Tenant propietario.</summary>
    public Guid ConcedidaPorUsuarioId { get; private set; }
    public DateTime ConcedidaEnUtc { get; private set; }

    /// <summary>Actor real que la revocó; nulo mientras está vigente.</summary>
    public Guid? RevocadaPorUsuarioId { get; private set; }
    public DateTime? RevocadaEnUtc { get; private set; }

    public Guid Version { get; private set; } = Guid.NewGuid();

    public bool EstaVigente => RevocadaEnUtc is null;

    private ConcesionCapacidadTenant()
    {
    }

    public static ConcesionCapacidadTenant Conceder(
        Guid usuarioId, CapacidadTenant capacidad, Guid concedidaPorUsuarioId, DateTime ahoraUtc)
    {
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("La concesión necesita la cuenta que la recibe.", nameof(usuarioId));
        if (!Enum.IsDefined(capacidad))
            throw new ArgumentOutOfRangeException(nameof(capacidad), capacidad, "Capacidad del Tenant desconocida.");
        if (concedidaPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La concesión necesita a quien la concede.", nameof(concedidaPorUsuarioId));

        return new ConcesionCapacidadTenant
        {
            UsuarioId = usuarioId,
            Capacidad = capacidad,
            ConcedidaPorUsuarioId = concedidaPorUsuarioId,
            ConcedidaEnUtc = ahoraUtc,
        };
    }

    public void Revocar(Guid revocadaPorUsuarioId, DateTime ahoraUtc)
    {
        if (!EstaVigente)
            throw new InvalidOperationException("La concesión ya estaba revocada.");
        if (revocadaPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La revocación necesita a quien revoca.", nameof(revocadaPorUsuarioId));

        RevocadaPorUsuarioId = revocadaPorUsuarioId;
        RevocadaEnUtc = ahoraUtc;
        _accionAuditoria = RegistroAuditoria.AccionCapacidadRevocada;
    }

    // Solo campo: sin propiedad, EF no lo mapea (patrón de ContratoAcceso).
    private string? _accionAuditoria;

    string? IAccionAuditoriaPropia.ConsumirAccionAuditoria()
    {
        var accion = _accionAuditoria;
        _accionAuditoria = null;
        return accion;
    }
}
