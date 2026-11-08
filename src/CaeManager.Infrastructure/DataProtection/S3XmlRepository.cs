using System.Xml.Linq;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace CaeManager.Infrastructure.DataProtection;

/// <summary>
/// Un objeto S3 por clave de Data Protection, bajo <see cref="DataProtectionS3Options.Prefijo"/>
/// — puede compartir bucket con otros usos, siempre bajo su propio prefijo.
///
/// <see cref="IXmlRepository"/> es una interfaz síncrona (así la definió
/// ASP.NET Core, pensada originalmente para disco local) y el SDK de AWS solo ofrece
/// métodos asíncronos en net8.0. El puente lo hace <see cref="LlamadaSincronaAws"/>:
/// ejecuta la llamada fuera del SynchronizationContext del llamante y con un límite de
/// tiempo, porque Data Protection puede llegar aquí desde el hilo de un circuito de
/// Blazor (carga en frío o recarga forzada del anillo), y un <c>GetAwaiter().GetResult()</c>
/// directo sobre un <c>await</c> que captura ese contexto se interbloquea.
/// </summary>
public class S3XmlRepository : IXmlRepository
{
    private readonly IAmazonS3 _cliente;
    private readonly DataProtectionS3Options _opciones;
    private readonly TimeSpan _limite;

    public S3XmlRepository(IAmazonS3 cliente, DataProtectionS3Options opciones)
        : this(cliente, opciones, LlamadaSincronaAws.LimitePorDefecto)
    {
    }

    internal S3XmlRepository(IAmazonS3 cliente, DataProtectionS3Options opciones, TimeSpan limite)
    {
        _cliente = cliente;
        _opciones = opciones;
        _limite = limite;
    }

    public IReadOnlyCollection<XElement> GetAllElements() =>
        LlamadaSincronaAws.Ejecutar(
            ObtenerTodosLosElementosAsync, _limite, "La lectura de las claves de Data Protection en S3");

    public void StoreElement(XElement element, string friendlyName) =>
        LlamadaSincronaAws.Ejecutar(
            token => AlmacenarElementoAsync(element, friendlyName, token),
            _limite,
            "La escritura de una clave de Data Protection en S3");

    private async Task<IReadOnlyCollection<XElement>> ObtenerTodosLosElementosAsync(CancellationToken token)
    {
        var elementos = new List<XElement>();

        var respuestaListado = await _cliente.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _opciones.BucketName,
            Prefix = _opciones.Prefijo
        }, token).ConfigureAwait(false);

        foreach (var objeto in respuestaListado.S3Objects)
        {
            using var respuestaObjeto = await _cliente
                .GetObjectAsync(_opciones.BucketName, objeto.Key, token)
                .ConfigureAwait(false);
            using var lector = new StreamReader(respuestaObjeto.ResponseStream);
            var contenido = await lector.ReadToEndAsync(token).ConfigureAwait(false);
            elementos.Add(XElement.Parse(contenido));
        }

        return elementos;
    }

    private async Task AlmacenarElementoAsync(XElement element, string friendlyName, CancellationToken token)
    {
        await _cliente.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _opciones.BucketName,
            Key = $"{_opciones.Prefijo}{friendlyName}.xml",
            ContentBody = element.ToString()
        }, token).ConfigureAwait(false);
    }
}
