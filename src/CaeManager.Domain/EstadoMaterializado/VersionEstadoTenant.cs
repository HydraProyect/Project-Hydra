using CaeManager.Domain.Common;

namespace CaeManager.Domain.EstadoMaterializado;

/// <summary>
/// Versión del estado materializado de un Tenant propietario: sube en 1 en cada transacción que escribe estado, como
/// último paso, y es lo que miran las cachés para saber si lo que tienen sigue valiendo. Una fila por Tenant, garantizada
/// por el índice único sobre <see cref="EntidadConTenant.TenantId"/>.
///
/// <para>
/// Sin constructor público ni métodos: esta entidad solo define la tabla.
/// </para>
/// </summary>
public class VersionEstadoTenant : EntidadConTenant
{
    public long Version { get; private set; }
    public DateTime ActualizadoEnUtc { get; private set; }

    private VersionEstadoTenant()
    {
        // Requerido por EF Core.
    }
}
