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

    /// <summary>La reclamación es la de un buzón compartido de Microsoft 365 (siempre con <see cref="IdentidadBuzon"/>).</summary>
    public bool EsBuzonCompartido { get; private set; }

    /// <summary>
    /// Desde cuándo esta reclamación sin <see cref="IdentidadBuzon"/> ya no cuenta para la guarda transitoria del alta
    /// de buzones compartidos (ver <see cref="CuentaParaGuardaTransitoria"/>). Null en las heredadas que siguen
    /// pendientes de completar su identidad.
    /// </summary>
    public DateTime? FueraDeGuardaTransitoriaUtc { get; private set; }

    /// <summary>
    /// Guarda transitoria del alta de buzones compartidos. Una reclamación anterior a <see cref="IdentidadBuzon"/> no
    /// está en el índice único por identidad: mientras no se complete, otro Tenant propietario podría reclamar el mismo
    /// buzón con un alias y desplazar a la conexión antigua, que es la legítima. Por eso, mientras exista en toda la
    /// plataforma una reclamación que cuente aquí, PostgreSQL rechaza la de un buzón compartido nuevo (restricción de
    /// exclusión <c>EX_ReclamacionesBuzonIntegracion_GuardaTransitoriaCompartidos</c>, que como los índices únicos se
    /// comprueba sin mirar la RLS ni leer filas de otro Tenant).
    /// <para>
    /// Cuentan solo las heredadas sin identidad de una conexión que todavía puede completarla. Dejan de contar cuando
    /// el ciclo diario completa la identidad, o cuando ve su conexión en un estado terminal (ConError, incluido un
    /// 403/404 del buzón, o de otro proveedor o ya sin conexión, o de un Tenant propietario que no está Activo, cuya
    /// suscripción ya no se renueva); Desconectar borra la reclamación. El ciclo diario recorre para esto también los
    /// Tenants no activos, cada uno en su ámbito, sin verificar ni renovar nada en ellos. Ninguna reclamación
    /// nueva cuenta: las de Microsoft 365 nacen con identidad, y una sin ella es de un proveedor que no la tiene.
    /// La guarda es transitoria y queda inerte cuando no hay ninguna que cuente.
    /// </para>
    /// </summary>
    public bool CuentaParaGuardaTransitoria => IdentidadBuzon is null && FueraDeGuardaTransitoriaUtc is null;

    public Guid TenantPropietarioId { get; private set; }
    public Guid ConexionIntegracionId { get; private set; }
    public DateTime ReclamadoEnUtc { get; private set; }

    private ReclamacionBuzonIntegracion()
    {
        // Requerido por EF Core.
    }

    public ReclamacionBuzonIntegracion(
        string buzonEmail, string? identidadBuzon, Guid tenantPropietarioId, Guid conexionIntegracionId, DateTime ahoraUtc,
        bool esBuzonCompartido = false)
    {
        if (esBuzonCompartido && identidadBuzon is null)
            throw new ArgumentException("La reclamación de un buzón compartido debe tener la identidad del buzón.", nameof(identidadBuzon));

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
        else
            FueraDeGuardaTransitoriaUtc = ahoraUtc; // Nueva y sin identidad: de un proveedor que no la tiene.

        EsBuzonCompartido = esBuzonCompartido;
        TenantPropietarioId = tenantPropietarioId;
        ConexionIntegracionId = conexionIntegracionId;
        ReclamadoEnUtc = ahoraUtc;
    }

    /// <summary>
    /// El ciclo diario vio la conexión de esta reclamación heredada en un estado terminal, o su Tenant propietario no
    /// está Activo: deja de contar para la guarda
    /// transitoria (<see cref="CuentaParaGuardaTransitoria"/>). No vuelve a contar: si la conexión completa más tarde su
    /// identidad, el índice único por identidad decide.
    /// </summary>
    /// <returns>true si cambió.</returns>
    public bool SacarDeGuardaTransitoria(DateTime ahoraUtc)
    {
        if (!CuentaParaGuardaTransitoria)
            return false;

        FueraDeGuardaTransitoriaUtc = ahoraUtc;
        return true;
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
