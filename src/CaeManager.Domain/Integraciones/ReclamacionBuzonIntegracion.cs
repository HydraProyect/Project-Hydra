using CaeManager.Domain.Common;

namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Reclamación exclusiva de un buzón por el Tenant propietario que lo conectó primero
/// (PROPUESTA-BUZONES-COMPARTIDOS-M365 § 5.4, incremento 1). Antes de esta entidad no existía ninguna
/// unicidad global de buzón: el mismo correo podía quedar conectado en dos Tenants a la vez, cada uno
/// leyendo/respondiendo el mismo hilo sin saber del otro.
///
/// <see cref="TenantPropietarioId"/> identifica al Tenant propietario y la política RLS limita las filas
/// visibles y modificables a ese Tenant. Dos índices únicos conservan la unicidad global entre Tenants
/// también con RLS: el de <see cref="BuzonEmail"/> y el de <see cref="IdentidadBuzon"/>. El segundo es el
/// que cierra los alias: un buzón compartido se da de alta con la dirección que alguien escribe, y el
/// correo principal, el UPN y cualquier proxy del mismo buzón son textos distintos con la misma
/// identidad. El intento de inserción traduce el choque de cualquiera de los dos (23505) a "buzón ya
/// conectado", mismo patrón que <c>OperacionImportacionRepository.GuardarSiOperacionNuevaAsync</c>.
/// </summary>
public class ReclamacionBuzonIntegracion : Entity
{
    public const int LongitudMaximaBuzonEmail = ConexionIntegracion.LongitudMaximaBuzonEmail;

    /// <summary>Tope de <see cref="IdentidadBuzon"/>: un Id inmutable de carpeta de Graph ronda los 150 caracteres.</summary>
    public const int LongitudMaximaIdentidadBuzon = 512;

    public string BuzonEmail { get; private set; } = string.Empty;

    /// <summary>
    /// Identidad estable del buzón (<see cref="ConexionIntegracion.IdentidadBuzon"/>). Toda reclamación nueva de
    /// Microsoft 365 la lleva; null en las anteriores a este campo, hasta que la renovación de la suscripción la
    /// completa, y en las de un proveedor sin esa identidad.
    /// Se compara tal cual, sin normalizar: es un Id de Graph, no una dirección.
    /// </summary>
    public string? IdentidadBuzon { get; private set; }

    public Guid TenantPropietarioId { get; private set; }
    public Guid ConexionIntegracionId { get; private set; }
    public DateTime ReclamadoEnUtc { get; private set; }

    private ReclamacionBuzonIntegracion()
    {
        // Requerido por EF Core.
    }

    public ReclamacionBuzonIntegracion(
        string buzonEmail, string? identidadBuzon, Guid tenantPropietarioId, Guid conexionIntegracionId, DateTime ahoraUtc)
    {
        if (string.IsNullOrWhiteSpace(buzonEmail))
            throw new ArgumentException("La reclamación debe tener un buzón.", nameof(buzonEmail));

        // Normalizado a minúsculas SOLO en esta tabla de guarda: el correo
        // que se muestra al usuario (ConexionIntegracion.BuzonEmail) no se
        // toca. Sin esto, "CAE@tenant.com" y "cae@tenant.com" pasarían el
        // índice único como dos buzones distintos — el dominio (RFC 5321) es
        // case-insensitive en la práctica para todos los proveedores relevantes.
        var normalizado = buzonEmail.Trim().ToLowerInvariant();
        if (normalizado.Length > LongitudMaximaBuzonEmail)
            throw new ArgumentException($"El buzón no puede superar {LongitudMaximaBuzonEmail} caracteres.", nameof(buzonEmail));

        if (tenantPropietarioId == Guid.Empty)
            throw new ArgumentException("La reclamación debe tener un Tenant propietario.", nameof(tenantPropietarioId));

        if (conexionIntegracionId == Guid.Empty)
            throw new ArgumentException("La reclamación debe referenciar la conexión que la originó.", nameof(conexionIntegracionId));

        BuzonEmail = normalizado;
        if (identidadBuzon is not null)
            FijarIdentidadBuzon(identidadBuzon);
        TenantPropietarioId = tenantPropietarioId;
        ConexionIntegracionId = conexionIntegracionId;
        ReclamadoEnUtc = ahoraUtc;
    }

    /// <summary>
    /// Completa la identidad de una reclamación anterior a <see cref="IdentidadBuzon"/>. Una vez fijada no
    /// cambia: otra identidad sería otro buzón.
    /// </summary>
    public void FijarIdentidadBuzon(string identidadBuzon)
    {
        if (string.IsNullOrWhiteSpace(identidadBuzon) || identidadBuzon.Length > LongitudMaximaIdentidadBuzon)
            throw new ArgumentException("La reclamación debe tener la identidad del buzón.", nameof(identidadBuzon));

        if (IdentidadBuzon is not null && !string.Equals(IdentidadBuzon, identidadBuzon, StringComparison.Ordinal))
            throw new InvalidOperationException("La reclamación ya tiene la identidad de otro buzón.");

        IdentidadBuzon = identidadBuzon;
    }
}
