namespace CaeManager.Application.Common.Archivos;

/// <summary>
/// Forma de las claves del almacenamiento de archivos: cada clave empieza por la carpeta de su Tenant propietario
/// (<c>{tenantId:N}/archivo</c>, la que escribe <see cref="IFileStorageService.GuardarAsync"/> bajo el Tenant activo). La
/// regla es del Núcleo porque el almacén lo es (ADR-013, K-1): la usan el requisito documental de un Centro, para las
/// claves que llegan del cliente, e Inbound, para sus originales (<c>OriginalesInbound.EsDelTenant</c> delega aquí).
/// </summary>
public static class ClavesDeAlmacen
{
    /// <summary>
    /// ¿Es la referencia un archivo directo de la carpeta de <paramref name="tenantPropietarioId"/>? Sin subcarpetas ni
    /// segmentos <c>..</c>: una referencia que no lo cumple no es de ese Tenant, aunque empiece por su carpeta.
    /// </summary>
    public static bool EsDeLaCarpetaDelTenant(string referencia, Guid tenantPropietarioId) =>
        referencia.StartsWith($"{tenantPropietarioId:N}/", StringComparison.Ordinal)
        && referencia.Length > 33
        && referencia.IndexOf('/', 33) < 0
        && !referencia.Contains("..", StringComparison.Ordinal);
}
