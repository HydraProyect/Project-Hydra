using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace CaeManager.Infrastructure.DataProtection;

/// <summary>
/// Contrapartida de <see cref="KmsXmlEncryptor"/>. Data Protection lo
/// instancia a través del contenedor de servicios usando el
/// <c>decryptorType</c> que quedó escrito en el XML, por eso recibe
/// <see cref="IServiceProvider"/> y no las opciones directamente.
///
/// No hace falta indicar la clave: el texto cifrado de KMS lleva dentro qué
/// clave lo cifró, así que descifrar sigue funcionando después de rotarla —
/// que es justo lo que permite rotar sin quedarse sin poder leer lo anterior.
/// </summary>
public class KmsXmlDecryptor : IXmlDecryptor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeSpan _limite;

    // Data Protection construye el descifrador con este constructor público
    // (recibe IServiceProvider); el interno solo existe para acotar el límite
    // de tiempo en los tests.
    public KmsXmlDecryptor(IServiceProvider serviceProvider)
        : this(serviceProvider, LlamadaSincronaAws.LimitePorDefecto)
    {
    }

    internal KmsXmlDecryptor(IServiceProvider serviceProvider, TimeSpan limite)
    {
        _serviceProvider = serviceProvider;
        _limite = limite;
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        var kms = _serviceProvider.GetService(typeof(IAmazonKeyManagementService)) as IAmazonKeyManagementService
            ?? throw new InvalidOperationException(
                "Hay claves de Data Protection cifradas con KMS pero KMS no está configurado. " +
                "Sin él no se pueden descifrar las credenciales guardadas: revisa DataProtection:Kms en la configuración.");

        var cifrado = Convert.FromBase64String(encryptedElement.Value);

        // Data Protection descifra el descriptor de una clave la primera vez que
        // la usa, en el hilo de quien la usa (que puede ser un circuito de
        // Blazor): ver LlamadaSincronaAws para el porqué del puente.
        // Si la respuesta llega después del límite, nadie la usa: su texto plano se
        // borra en cuanto llega en vez de quedar en una tarea abandonada.
        var respuesta = LlamadaSincronaAws.Ejecutar(
            token => kms.DecryptAsync(new DecryptRequest
            {
                CiphertextBlob = new MemoryStream(cifrado)
            }, token),
            _limite,
            "El descifrado con KMS de una clave de Data Protection",
            static tardia => BorrarFlujo(tardia.Plaintext));

        var textoPlano = respuesta.Plaintext.ToArray();
        try
        {
            return XElement.Parse(Encoding.UTF8.GetString(textoPlano));
        }
        finally
        {
            // El XElement devuelto es lo que Data Protection necesita; las copias
            // intermedias en bytes no tienen por qué sobrevivir a esta llamada.
            CryptographicOperations.ZeroMemory(textoPlano);
            BorrarFlujo(respuesta.Plaintext);
        }
    }

    /// <summary>
    /// Sobrescribe con ceros el contenido de un flujo en memoria si admite escritura y
    /// desplazamiento, y lo libera. Que el SDK construya <c>Plaintext</c> como un
    /// <see cref="MemoryStream"/> escribible sobre los bytes recibidos es una suposición
    /// no verificada contra AWSSDK.KeyManagementService: si no lo fuera, el texto plano
    /// no se borra (sin aviso) y solo se libera el flujo. No lanza: también se usa sobre
    /// respuestas tardías, donde nadie observaría el fallo.
    /// </summary>
    internal static void BorrarFlujo(MemoryStream? flujo)
    {
        if (flujo is null)
        {
            return;
        }

        try
        {
            if (flujo.CanWrite && flujo.CanSeek)
            {
                var longitud = checked((int)flujo.Length);
                flujo.Position = 0;
                flujo.Write(new byte[longitud]);
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or ObjectDisposedException or IOException or OverflowException)
        {
            // Un flujo que no se deja sobrescribir se libera igualmente.
        }
        finally
        {
            flujo.Dispose();
        }
    }
}
