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
    /// ¿Empieza la referencia por la carpeta de <paramref name="tenantPropietarioId"/> (<c>{tenantId:N}/</c>), sin otra
    /// <c>/</c> después, con al menos un carácter tras ella y sin ningún <c>..</c>? Es exactamente lo que comprueba, y nada
    /// más: la barra invertida no se trata como separador, así que <c>{tenantId:N}/sub\archivo</c> pasa (deuda declarada
    /// en ADR-013 M2; cerrarla cambiaría qué claves se admiten, y no es un movimiento de código).
    /// </summary>
    public static bool EsDeLaCarpetaDelTenant(string referencia, Guid tenantPropietarioId) =>
        referencia.StartsWith($"{tenantPropietarioId:N}/", StringComparison.Ordinal)
        && referencia.Length > 33
        && referencia.IndexOf('/', 33) < 0
        && !referencia.Contains("..", StringComparison.Ordinal);
}
