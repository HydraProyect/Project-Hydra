using CaeManager.Application.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.ContratosAcceso.LecturaInbound;

namespace CaeManager.Application.Tests.ContratosAcceso;

/// <summary>
/// Almacenamiento de originales del Tenant de la Empresa contratista, en memoria. Indexado por (Tenant, referencia), como
/// el de disco, que solo descifra con la clave del Tenant al que pertenece la carpeta: abrir con otro Tenant no encuentra
/// nada. Apunta cada apertura para que el test vea qué se abrió y en qué orden.
/// </summary>
public class AlmacenOriginalesInboundFalso(List<string>? orden = null) : IAlmacenOriginalesInbound
{
    public Dictionary<(Guid Tenant, string Referencia), byte[]> Archivos { get; } = [];

    public List<(Guid Tenant, string Referencia)> Aperturas { get; } = [];

    public Task<Stream> AbrirOriginalAsync(ArchivoInboundDto archivo, CancellationToken cancellationToken = default)
    {
        var identificador = archivo.ArchivoUrl ?? string.Empty;
        Aperturas.Add((archivo.TenantPropietarioId, identificador));
        orden?.Add("AbrirOriginal");
        return Archivos.TryGetValue((archivo.TenantPropietarioId, identificador), out var contenido)
            ? Task.FromResult<Stream>(new MemoryStream(contenido, writable: false))
            : throw new FileNotFoundException("Sin original en el almacenamiento falso.", identificador);
    }
}
