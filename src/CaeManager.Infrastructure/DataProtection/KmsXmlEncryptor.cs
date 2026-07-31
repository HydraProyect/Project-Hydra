using System.Text;
using System.Xml.Linq;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace CaeManager.Infrastructure.DataProtection;

/// <summary>
/// Cifra cada clave de Data Protection con AWS KMS antes de escribirla en
/// disco (ver <see cref="DataProtectionKmsOptions"/> para el porqué).
///
/// Se implementa contra el SDK oficial de AWS en vez de usar uno de los
/// paquetes de terceros que existen para esto: son ~50 líneas, van en la ruta
/// criptográfica de la aplicación, y ahí una dependencia comunitaria sin
/// mantenimiento garantizado cuesta más de lo que ahorra. El SDK oficial ya
/// estaba en el proyecto para los backups a S3.
///
/// El texto cifrado se guarda en Base64 dentro de un elemento propio; el
/// descifrado lo hace <see cref="KmsXmlDecryptor"/>, que Data Protection
/// localiza por el atributo <c>decryptorType</c> que se escribe aquí.
/// </summary>
public class KmsXmlEncryptor(IAmazonKeyManagementService kms, string keyId) : IXmlEncryptor
{
    internal const string NombreElemento = "cifradoConKms";

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);

        var textoPlano = plaintextElement.ToString(SaveOptions.DisableFormatting);

        // GetAwaiter().GetResult(): la interfaz de Data Protection es
        // síncrona y no hay variante asíncrona. Ocurre una vez por creación
        // de clave (cada 90 días por defecto), no en el camino caliente.
        var respuesta = kms.EncryptAsync(new EncryptRequest
        {
            KeyId = keyId,
            Plaintext = new MemoryStream(Encoding.UTF8.GetBytes(textoPlano))
        }).GetAwaiter().GetResult();

        var cifrado = Convert.ToBase64String(respuesta.CiphertextBlob.ToArray());

        return new EncryptedXmlInfo(
            new XElement(NombreElemento, cifrado),
            typeof(KmsXmlDecryptor));
    }
}
