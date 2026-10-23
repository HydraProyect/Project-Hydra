using CaeManager.Application.ContratosAcceso.LecturaInbound;
using CaeManager.Domain.EvidenciasInbound;

namespace CaeManager.Application.ContratosAcceso.EvidenciasInbound;

public interface IEvidenciasInboundQueryContext
{
    IQueryable<EvidenciaInbound> EvidenciasInbound { get; }
}

/// <summary>
/// Lectura del binario de un Documento original del Tenant de la Empresa contratista desde la petición del Tenant del
/// Cliente empresarial (Inbound M1, PR 4). El almacenamiento está particionado y cifrado por Tenant, así que el Tenant
/// propietario del archivo va explícito: es el <c>TenantContratistaId</c> del contrato de acceso, nunca una entrada del
/// usuario. Recibe el <see cref="ArchivoInboundDto"/> entero que devolvió <c>app_inbound_archivo_autorizado</c>, con el
/// Tenant propietario que la propia función tomó del contrato, y no un par libre (Tenant, cadena). Solo la consumen los
/// handlers que antes deciden la autoridad (<see cref="LecturaInbound.LecturaInboundAutoridad"/>), en Application, Web e
/// Infrastructure (<c>ConsumidoresDelLectorInboundAcotadosTests</c>), y el tipo concreto que la implementa no se referencia
/// fuera de su registro de dependencias (<c>AlmacenDeDiscoSoloPorSusPuertosTests</c>).
/// </summary>
public interface IAlmacenOriginalesInbound
{
    /// <summary>
    /// El contenido descifrado del original. <see cref="FileNotFoundException"/> si no tiene referencia, si la referencia
    /// no es de la carpeta de <see cref="ArchivoInboundDto.TenantPropietarioId"/> o si no existe.
    /// </summary>
    Task<Stream> AbrirOriginalAsync(ArchivoInboundDto archivo, CancellationToken cancellationToken = default);
}

/// <summary>Reglas comunes a quien abre un original Inbound: de qué Tenant es la referencia y con qué tipo MIME se sirve.</summary>
public static class OriginalesInbound
{
    /// <summary>
    /// ¿Es la referencia de la carpeta del Tenant de la Empresa contratista? Las referencias del almacenamiento empiezan por
    /// la carpeta de su Tenant propietario (<c>{tenantId:N}/archivo</c>). Una que no lo cumple no se abre: la función de
    /// la base devolvió algo que no es del Tenant del contrato, y servirla sería leer otra cosa con su autorización.
    /// </summary>
    public static bool EsDelTenant(string referencia, Guid tenantPropietarioId) =>
        referencia.StartsWith($"{tenantPropietarioId:N}/", StringComparison.Ordinal)
        && referencia.Length > 33
        && referencia.IndexOf('/', 33) < 0
        && !referencia.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// ¿Se puede abrir esta apertura para el contrato cuyo Tenant de la Empresa contratista es
    /// <paramref name="tenantDelContrato"/>? El Tenant que devolvió la función tiene que ser el del contrato, y la referencia
    /// tiene que ser de su carpeta.
    /// </summary>
    public static bool EsDelTenantDelContrato(ArchivoInboundDto archivo, Guid tenantDelContrato) =>
        archivo.TenantPropietarioId == tenantDelContrato
        && archivo.ArchivoUrl is { } referencia
        && EsDelTenant(referencia, archivo.TenantPropietarioId);

    public static string TipoMime(string referencia) =>
        Path.GetExtension(referencia).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => "application/octet-stream",
        };

    public static async Task<byte[]> LeerAsync(
        IAlmacenOriginalesInbound almacen, ArchivoInboundDto archivo, CancellationToken cancellationToken)
    {
        await using var flujo = await almacen.AbrirOriginalAsync(archivo, cancellationToken);
        using var memoria = new MemoryStream();
        await flujo.CopyToAsync(memoria, cancellationToken);
        return memoria.ToArray();
    }
}

/// <summary>Una evidencia Inbound, sin la referencia de su archivo: el binario se sirve aparte.</summary>
public record EvidenciaInboundDto(
    Guid Id,
    Guid ContratoAccesoId,
    Guid TenantOrigenId,
    Guid DocumentoOrigenId,
    Guid AutorizacionId,
    Guid TipoDocumentoId,
    string TipoDocumentoNombre,
    Guid? TrabajadorOrigenId,
    string SujetoNombre,
    string? SujetoDocumentoIdentidad,
    DateOnly FechaEmision,
    DateOnly? FechaVencimiento,
    bool SoloConclusionAptitud,
    ConclusionAptitud? ConclusionAptitud,
    bool TieneArchivo,
    string? HuellaSha256,
    Domain.Documentos.EstadoAcreditacion Estado,
    Domain.Documentos.CausaRechazoAcreditacion? CausaRechazo,
    string? MotivoRechazo,
    Guid ValidadaPorUsuarioId,
    DateTime ValidadaEnUtc);
