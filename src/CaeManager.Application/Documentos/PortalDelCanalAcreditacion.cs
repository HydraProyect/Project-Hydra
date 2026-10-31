using CaeManager.Application.Centros;
using CaeManager.Application.Integraciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos;

/// <summary>
/// Lo que «Lo he enviado» y la entrega del archivo a la extensión preguntan al catálogo sobre el Proveedor del canal de
/// una acreditación: si está activo (kill switch remoto, MVP2 § 14.5), si un host es suyo y cómo se llama.
/// </summary>
internal static class PortalDelCanalAcreditacion
{
    public static async Task<bool> ProveedorActivoAsync(
        Guid canalGestionDocumentalId, ICentrosQueryContext centrosContext, IProveedoresPlataformaCaeQueryContext proveedoresContext,
        CancellationToken cancellationToken)
    {
        var proveedorId = await ProveedorDelCanalAsync(canalGestionDocumentalId, centrosContext, cancellationToken);
        if (proveedorId is null) return true; // sin proveedor de catálogo, nada que apagar

        return await proveedoresContext.ProveedoresPlataformaCae
            .Where(p => p.Id == proveedorId.Value)
            .Select(p => p.Activo)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public static async Task<bool> HostEsDelProveedorAsync(
        Guid canalGestionDocumentalId, string host, ICentrosQueryContext centrosContext,
        IProveedoresPlataformaCaeQueryContext proveedoresContext, CancellationToken cancellationToken)
    {
        var proveedorId = await ProveedorDelCanalAsync(canalGestionDocumentalId, centrosContext, cancellationToken);
        if (proveedorId is null) return false; // sin Proveedor no hay dominios con los que comparar

        var dominios = await proveedoresContext.DominiosProveedorPlataformaCae
            .Where(d => d.ProveedorPlataformaCaeId == proveedorId.Value)
            .Select(d => d.Dominio)
            .ToListAsync(cancellationToken);

        return dominios.Any(dominio => ResolucionProveedorPlataformaCaeService.CoincideHost(host, dominio));
    }

    /// <summary>
    /// El nombre del catálogo global (<c>ProveedorPlataformaCae</c>), lo único que el detalle de un portal ajeno puede
    /// nombrar: nada de la acreditación, del Trabajador ni del Tenant. <c>null</c> si el canal no tiene Proveedor o este
    /// no tiene nombre.
    /// </summary>
    public static async Task<string?> NombreProveedorAsync(
        Guid canalGestionDocumentalId, ICentrosQueryContext centrosContext, IProveedoresPlataformaCaeQueryContext proveedoresContext,
        CancellationToken cancellationToken)
    {
        var proveedorId = await ProveedorDelCanalAsync(canalGestionDocumentalId, centrosContext, cancellationToken);
        if (proveedorId is null) return null;

        var nombre = await proveedoresContext.ProveedoresPlataformaCae
            .Where(p => p.Id == proveedorId.Value)
            .Select(p => p.Nombre)
            .SingleOrDefaultAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(nombre) ? null : nombre;
    }

    private static Task<Guid?> ProveedorDelCanalAsync(
        Guid canalGestionDocumentalId, ICentrosQueryContext centrosContext, CancellationToken cancellationToken) =>
        centrosContext.CanalesGestionDocumental
            .Where(c => c.Id == canalGestionDocumentalId)
            .Select(c => c.ProveedorPlataformaCaeId)
            .SingleOrDefaultAsync(cancellationToken);
}
