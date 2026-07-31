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
public class KmsXmlDecryptor(IServiceProvider serviceProvider) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        var kms = serviceProvider.GetService(typeof(IAmazonKeyManagementService)) as IAmazonKeyManagementService
            ?? throw new InvalidOperationException(
                "Hay claves de Data Protection cifradas con KMS pero KMS no está configurado. " +
                "Sin él no se pueden descifrar las credenciales guardadas: revisa DataProtection:Kms en la configuración.");

        var cifrado = Convert.FromBase64String(encryptedElement.Value);

        var respuesta = kms.DecryptAsync(new DecryptRequest
        {
            CiphertextBlob = new MemoryStream(cifrado)
        }).GetAwaiter().GetResult();

        return XElement.Parse(Encoding.UTF8.GetString(respuesta.Plaintext.ToArray()));
    }
}
