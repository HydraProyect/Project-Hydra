using CaeManager.Application.Common.Archivos;

namespace CaeManager.Infrastructure.FileStorage;

/// <summary>
/// <see cref="IAlmacenArchivosDelTenantPropietario"/> sobre el almacén de disco: la lectura que cruza Tenants, en una
/// instancia distinta de la que el contenedor entrega como <c>IFileStorageService</c>. <see cref="DiskFileStorageService"/>
/// no implementa este puerto y su método es <c>internal</c>, así que desde Application y Web no se llega a esta lectura
/// con un cast ni con <c>dynamic</c>. Dentro de Infrastructure, <c>internal</c> no basta: lo que impide que otro tipo la
/// alcance (por <c>dynamic</c>, construyendo esta clase o pidiendo el concreto) son los barridos de
/// <c>AlmacenDeDiscoSoloPorSusPuertosTests</c>. Es <c>internal</c> por lo mismo: fuera de Infrastructure solo se obtiene
/// por el puerto. Delega sin lógica propia; la regla de carpeta la aplica el almacén antes de tocar el disco.
/// </summary>
internal sealed class LecturaDeDiscoDelTenantPropietario(DiskFileStorageService disco) : IAlmacenArchivosDelTenantPropietario
{
    public Task<Stream> AbrirDelTenantPropietarioAsync(
        string identificador, Guid tenantPropietarioId, CancellationToken cancellationToken = default) =>
        disco.AbrirDelTenantPropietarioAsync(identificador, tenantPropietarioId, cancellationToken);
}
