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

    /// <remarks>
    /// El límite se aplica a cada petición (el listado y cada descarga), no a la lectura
    /// entera: un anillo grande o una latencia que se acumula entre peticiones que
    /// responden bien no debe impedir cargarlo. El presupuesto total es, por tanto,
    /// (1 + objetos) × límite como mucho; el listado lee una sola página (hasta 1.000
    /// objetos, como antes de existir el límite), y un anillo de Data Protection tiene
    /// del orden de una clave por cada 90 días.
    /// </remarks>
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var claves = LlamadaSincronaAws.Ejecutar(
            ListarClavesAsync, _limite, "El listado de las claves de Data Protection en S3");

        var elementos = new List<XElement>(claves.Count);
        foreach (var clave in claves)
        {
            var contenido = LlamadaSincronaAws.Ejecutar(
                token => LeerObjetoAsync(clave, token),
                _limite,
                "La lectura de una clave de Data Protection en S3");
            elementos.Add(XElement.Parse(contenido));
        }

        return elementos;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        // La clausura solo retiene lo que viaja a S3, no el elemento de Data Protection.
        var clave = $"{_opciones.Prefijo}{friendlyName}.xml";
        var contenido = element.ToString();

        LlamadaSincronaAws.Ejecutar(
            token => AlmacenarAsync(clave, contenido, token),
            _limite,
            "La escritura de una clave de Data Protection en S3");
    }

    private async Task<IReadOnlyList<string>> ListarClavesAsync(CancellationToken token)
    {
        var respuestaListado = await _cliente.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _opciones.BucketName,
            Prefix = _opciones.Prefijo
        }, token).ConfigureAwait(false);

        // AWSSDK.S3 4.x deja S3Objects a null si el servicio no devuelve objetos
        // (AWSConfigs.InitializeCollections es false por defecto): un prefijo vacío
        // es un anillo vacío, no un error.
        return respuestaListado.S3Objects?.Select(objeto => objeto.Key).ToList() ?? [];
    }

    private async Task<string> LeerObjetoAsync(string clave, CancellationToken token)
    {
        using var respuestaObjeto = await _cliente
            .GetObjectAsync(_opciones.BucketName, clave, token)
            .ConfigureAwait(false);
        using var lector = new StreamReader(respuestaObjeto.ResponseStream);
        return await lector.ReadToEndAsync(token).ConfigureAwait(false);
    }

    private async Task AlmacenarAsync(string clave, string contenido, CancellationToken token)
    {
        await _cliente.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _opciones.BucketName,
            Key = clave,
            ContentBody = contenido
        }, token).ConfigureAwait(false);
    }
}
