using CaeManager.Application.Common.Archivos;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Inbound.ContratosAcceso.LecturaInbound;

namespace CaeManager.Infrastructure.Inbound.FileStorage;

/// <summary>
/// <see cref="IAlmacenOriginalesInbound"/> sobre el almacén del Núcleo (ADR-013, K-3; hasta M2 lo implementaba el propio
/// almacén de disco). Abre un original del Tenant de la Empresa contratista desde la petición del Tenant del Cliente
/// empresarial, con el Tenant propietario que devolvió <c>app_inbound_archivo_autorizado</c> junto con la referencia, en el
/// mismo <see cref="ArchivoInboundDto"/>. Antes de tocar el almacén, la referencia tiene que existir y ser de la carpeta de
/// ese Tenant (<see cref="OriginalesInbound.EsDelTenant"/>); si no, «no existe», igual que antes. El almacén vuelve a
/// comprobar la carpeta, resuelve la ruta dentro de ella y descifra con la clave derivada de ese Tenant, nunca con la del
/// Tenant activo. La autorización la comprobó la función; la cartera, Application.
/// </summary>
public sealed class AdaptadorAlmacenOriginalesInbound(IAlmacenArchivosDelTenantPropietario almacen) : IAlmacenOriginalesInbound
{
    public async Task<Stream> AbrirOriginalAsync(ArchivoInboundDto archivo, CancellationToken cancellationToken = default)
    {
        var tenantPropietarioId = archivo.TenantPropietarioId;
        if (tenantPropietarioId == Guid.Empty || archivo.ArchivoUrl is not { } identificador
            || !OriginalesInbound.EsDelTenant(identificador, tenantPropietarioId))
            throw new FileNotFoundException("No encontramos el archivo solicitado.", archivo.ArchivoUrl);

        return await almacen.AbrirDelTenantPropietarioAsync(identificador, tenantPropietarioId, cancellationToken);
    }
}
