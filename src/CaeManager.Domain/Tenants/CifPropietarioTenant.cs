using CaeManager.Domain.Common;

namespace CaeManager.Domain.Tenants;

/// <summary>
/// CIF o NIF del Tenant propietario, normalizado con <see cref="ValidadorIdentificacion.Normalizar"/>.
/// <para>
/// Vive en su propia tabla y no en <c>Tenants</c>: el NIF de una persona física es un dato personal
/// (DA-11) y <c>Tenants</c> la leen sin restricción por Tenant los tres roles de la aplicación. La tabla
/// está cerrada (RLS con FORCE, sin políticas y sin ningún privilegio para los roles de la aplicación):
/// solo el rol propietario la lee o la escribe hasta que el alta autónoma abra su vía de acceso.
/// </para>
/// <para>
/// Una fila por Tenant (<see cref="TenantId"/> es la clave) y un CIF por Tenant entre todos los Tenants
/// (DM-5), con un índice único global. Un Tenant sin fila es un Tenant sin CIF declarado: los anteriores
/// al alta autónoma no se rellenan. No es el CIF de ninguna Empresa del Tenant: ese vive en
/// <c>Empresa</c> y es único solo dentro de su Tenant.
/// </para>
/// <para>
/// No hereda de <c>EntidadConTenant</c>: no es un dato operativo del Tenant, sino un identificador de
/// plataforma que solo usa el alta, y no lleva filtro de Tenant ni política de aislamiento.
/// </para>
/// </summary>
public sealed class CifPropietarioTenant
{
    public const int LongitudMaxima = 20;

    public Guid TenantId { get; private set; }

    public string Cif { get; private set; } = null!;

    private CifPropietarioTenant()
    {
        // Requerido por EF Core.
    }

    /// <summary>
    /// Declara el CIF del Tenant propietario. La unicidad entre Tenants la aplica la base, no este método.
    /// Cambiar el CIF de un Tenant propietario ya declarado no tiene regla de producto todavía.
    /// </summary>
    public static CifPropietarioTenant Declarar(Guid tenantId, string cif)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("El Tenant propietario es obligatorio.", nameof(tenantId));

        var normalizado = ValidadorIdentificacion.Normalizar(cif);
        if (normalizado is null || normalizado.Length > LongitudMaxima || !ValidadorIdentificacion.EsIdentificacionFiscalValida(normalizado))
            throw new ArgumentException("El CIF o NIF del Tenant propietario no es válido.", nameof(cif));

        return new CifPropietarioTenant { TenantId = tenantId, Cif = normalizado };
    }
}
