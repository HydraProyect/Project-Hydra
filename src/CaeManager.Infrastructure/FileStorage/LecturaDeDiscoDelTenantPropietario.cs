using CaeManager.Application.Common.Archivos;

namespace CaeManager.Infrastructure.FileStorage;

/// <summary>
/// <see cref="IAlmacenArchivosDelTenantPropietario"/> sobre el almacén de disco: la lectura que cruza Tenants, en una
/// instancia distinta de la que el contenedor entrega como <c>IFileStorageService</c>. Así, quien tenga el almacenamiento
/// del Tenant activo no puede llegar a esta lectura con un cast ni con <c>dynamic</c>: <see cref="DiskFileStorageService"/>
/// no implementa este puerto y su método es <c>internal</c>. Delega sin lógica propia; la regla de carpeta la aplica el
/// almacén antes de tocar el disco.
/// </summary>
public sealed class LecturaDeDiscoDelTenantPropietario(DiskFileStorageService disco) : IAlmacenArchivosDelTenantPropietario
{
    public Task<Stream> AbrirDelTenantPropietarioAsync(
        string identificador, Guid tenantPropietarioId, CancellationToken cancellationToken = default) =>
        disco.AbrirDelTenantPropietarioAsync(identificador, tenantPropietarioId, cancellationToken);
}
