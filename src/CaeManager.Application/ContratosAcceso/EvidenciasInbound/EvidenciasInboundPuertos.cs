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
/// usuario. Solo la consumen los handlers que antes deciden la autoridad (<see cref="LecturaInbound.LecturaInboundAutoridad"/>)
/// y obtienen la referencia de <c>app_inbound_archivo_autorizado</c> (<c>ConsumidoresDelLectorInboundAcotadosTests</c>).
/// </summary>
public interface IAlmacenOriginalesInbound
{
    /// <summary>
    /// El contenido descifrado del original. <see cref="FileNotFoundException"/> si la referencia no es de la carpeta de
    /// <paramref name="tenantPropietarioId"/> o no existe.
    /// </summary>
    Task<Stream> AbrirOriginalAsync(Guid tenantPropietarioId, string identificador, CancellationToken cancellationToken = default);
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

    public static string TipoMime(string referencia) =>
        Path.GetExtension(referencia).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => "application/octet-stream",
        };

    public static async Task<byte[]> LeerAsync(
        IAlmacenOriginalesInbound almacen, Guid tenantPropietarioId, string referencia, CancellationToken cancellationToken)
    {
        await using var flujo = await almacen.AbrirOriginalAsync(tenantPropietarioId, referencia, cancellationToken);
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
