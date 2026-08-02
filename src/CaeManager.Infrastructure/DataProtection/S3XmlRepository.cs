using System.Xml.Linq;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace CaeManager.Infrastructure.DataProtection;

/// <summary>
/// Un objeto S3 por clave de Data Protection, bajo <see cref="DataProtectionS3Options.Prefijo"/>
/// — mismo bucket que puede compartir <c>AlmacenamientoS3</c>, distinto prefijo.
///
/// <see cref="IXmlRepository"/> es una interfaz síncrona (así la definió
/// ASP.NET Core, pensada originalmente para disco local): <c>GetAwaiter().GetResult()</c>
/// sobre las llamadas async del SDK de AWS es lo que hace cualquier
/// implementación de Data Protection sobre almacenamiento en la nube, propio
/// o de terceros. No es una ruta caliente — solo se ejecuta al generar una
/// clave nueva (por defecto cada ~90 días) o al arrancar el proceso.
/// </summary>
public class S3XmlRepository(IAmazonS3 cliente, DataProtectionS3Options opciones) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements() =>
        ObtenerTodosLosElementosAsync().GetAwaiter().GetResult();

    public void StoreElement(XElement element, string friendlyName) =>
        AlmacenarElementoAsync(element, friendlyName).GetAwaiter().GetResult();

    private async Task<IReadOnlyCollection<XElement>> ObtenerTodosLosElementosAsync()
    {
        var elementos = new List<XElement>();

        var respuestaListado = await cliente.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = opciones.BucketName,
            Prefix = opciones.Prefijo
        });

        foreach (var objeto in respuestaListado.S3Objects)
        {
            using var respuestaObjeto = await cliente.GetObjectAsync(opciones.BucketName, objeto.Key);
            using var lector = new StreamReader(respuestaObjeto.ResponseStream);
            var contenido = await lector.ReadToEndAsync();
            elementos.Add(XElement.Parse(contenido));
        }

        return elementos;
    }

    private async Task AlmacenarElementoAsync(XElement element, string friendlyName)
    {
        await cliente.PutObjectAsync(new PutObjectRequest
        {
            BucketName = opciones.BucketName,
            Key = $"{opciones.Prefijo}{friendlyName}.xml",
            ContentBody = element.ToString()
        });
    }
}
