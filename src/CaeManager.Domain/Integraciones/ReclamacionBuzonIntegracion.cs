using CaeManager.Domain.Common;

namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Reclamación exclusiva de un <see cref="ConexionIntegracion.BuzonEmail"/> por
/// el Tenant propietario que lo conectó primero (PROPUESTA-BUZONES-COMPARTIDOS-M365
/// § 5.4, incremento 1). Antes de esta entidad no existía ninguna unicidad
/// global de buzón: el mismo correo podía quedar conectado en dos Tenants a
/// la vez, cada uno leyendo/respondiendo el mismo hilo sin saber del otro.
///
/// <see cref="TenantPropietarioId"/> identifica al Tenant propietario y la
/// política RLS limita las filas visibles y modificables a ese Tenant. El
/// índice único de <see cref="BuzonEmail"/> conserva la unicidad global entre
/// Tenants también con RLS. El intento de inserción traduce el choque del
/// índice único (23505) a "buzón ya conectado", mismo patrón que
/// <c>OperacionImportacionRepository.GuardarSiOperacionNuevaAsync</c>.
/// </summary>
public class ReclamacionBuzonIntegracion : Entity
{
    public const int LongitudMaximaBuzonEmail = ConexionIntegracion.LongitudMaximaBuzonEmail;

    public string BuzonEmail { get; private set; } = string.Empty;
    public Guid TenantPropietarioId { get; private set; }
    public Guid ConexionIntegracionId { get; private set; }
    public DateTime ReclamadoEnUtc { get; private set; }

    private ReclamacionBuzonIntegracion()
    {
        // Requerido por EF Core.
    }

    public ReclamacionBuzonIntegracion(string buzonEmail, Guid tenantPropietarioId, Guid conexionIntegracionId, DateTime ahoraUtc)
    {
        if (string.IsNullOrWhiteSpace(buzonEmail))
            throw new ArgumentException("La reclamación debe tener un buzón.", nameof(buzonEmail));

        // Normalizado a minúsculas SOLO en esta tabla de guarda: el correo
        // que se muestra al usuario (ConexionIntegracion.BuzonEmail) no se
        // toca. Sin esto, "CAE@tenant.com" y "cae@tenant.com" pasarían el
        // índice único como dos buzones distintos y la guarda no cerraría
        // nada — el dominio (RFC 5321) es case-insensitive en la práctica
        // para todos los proveedores relevantes.
        var normalizado = buzonEmail.Trim().ToLowerInvariant();
        if (normalizado.Length > LongitudMaximaBuzonEmail)
            throw new ArgumentException($"El buzón no puede superar {LongitudMaximaBuzonEmail} caracteres.", nameof(buzonEmail));

        if (tenantPropietarioId == Guid.Empty)
            throw new ArgumentException("La reclamación debe tener un Tenant propietario.", nameof(tenantPropietarioId));

        if (conexionIntegracionId == Guid.Empty)
            throw new ArgumentException("La reclamación debe referenciar la conexión que la originó.", nameof(conexionIntegracionId));

        BuzonEmail = normalizado;
        TenantPropietarioId = tenantPropietarioId;
        ConexionIntegracionId = conexionIntegracionId;
        ReclamadoEnUtc = ahoraUtc;
    }
}
