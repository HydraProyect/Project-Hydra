using CaeManager.Domain.Common;

namespace CaeManager.Domain.Tenants;

/// <summary>
/// Organización que compra y utiliza Hydra — la frontera absoluta de
/// aislamiento del sistema (ver docs/MULTITENANCY.md § 1). No es lo mismo que
/// la entidad <c>Cliente</c> del dominio CAE (empresa a la que un Tenant
/// presta servicio de coordinación) — un Tenant puede tener muchos Clientes.
///
/// Deliberadamente extiende <see cref="Entity"/>, no <see cref="EntidadBase"/>
/// ni <see cref="EntidadConTenant"/>: un Tenant no pertenece a sí mismo (no
/// tiene <c>TenantId</c>), y su ciclo de vida operativo se modela con
/// <see cref="EstadoTenant"/> (Activo/Suspendido), no con soft delete —
/// suspender un tenant es una operación de negocio explícita, no un borrado.
/// </summary>
public class Tenant : Entity
{
    public const int LongitudMaximaNombre = 200;

    public string Nombre { get; private set; } = string.Empty;
    public EstadoTenant Estado { get; private set; }
    public DateTime CreadoEnUtc { get; private set; } = DateTime.UtcNow;

    private Tenant()
    {
        // Requerido por EF Core.
    }

    public Tenant(string nombre)
    {
        EstablecerNombre(nombre);
        Estado = EstadoTenant.Activo;
    }

    public void Suspender() => Estado = EstadoTenant.Suspendido;

    public void Reactivar() => Estado = EstadoTenant.Activo;

    public void RenombrarA(string nombre) => EstablecerNombre(nombre);

    private void EstablecerNombre(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre del tenant es obligatorio.", nameof(nombre));

        var normalizado = nombre.Trim();

        if (normalizado.Length > LongitudMaximaNombre)
            throw new ArgumentException(
                $"El nombre del tenant no puede superar {LongitudMaximaNombre} caracteres.", nameof(nombre));

        Nombre = normalizado;
    }
}
