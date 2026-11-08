using System.Security.Cryptography;
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
public class KmsXmlEncryptor : IXmlEncryptor
{
    internal const string NombreElemento = "cifradoConKms";

    private readonly IAmazonKeyManagementService _kms;
    private readonly string _keyId;
    private readonly TimeSpan _limite;
    private readonly TaskScheduler _planificador;

    public KmsXmlEncryptor(IAmazonKeyManagementService kms, string keyId)
        : this(kms, keyId, LlamadaSincronaAws.LimitePorDefecto)
    {
    }

    internal KmsXmlEncryptor(IAmazonKeyManagementService kms, string keyId, TimeSpan limite)
        : this(kms, keyId, limite, TaskScheduler.Default)
    {
    }

    /// <param name="planificador">Solo los tests pasan otro que <see cref="TaskScheduler.Default"/>.</param>
    internal KmsXmlEncryptor(IAmazonKeyManagementService kms, string keyId, TimeSpan limite, TaskScheduler planificador)
    {
        _kms = kms;
        _keyId = keyId;
        _limite = limite;
        _planificador = planificador;
    }

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);

        // La clausura solo retiene estos bytes, que se borran al salir aunque la
        // petición quede abandonada tras el límite: si aún no se había serializado,
        // KMS recibe ceros y el resultado ya nadie lo usa. No alcanza a las copias
        // que el SDK haga al serializar ni al string intermedio, que es inmutable y
        // vive lo que tarde el recolector.
        var textoPlano = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        EncryptResponse respuesta;
        try
        {
            // La interfaz de Data Protection es síncrona y el SDK de KMS solo es
            // asíncrono: LlamadaSincronaAws ejecuta la llamada fuera del
            // SynchronizationContext del llamante y con un límite de tiempo. Ocurre
            // una vez por creación de clave (cada 90 días por defecto), pero puede
            // ocurrir en el hilo de un circuito de Blazor si coincide con una carga
            // en frío o una recarga forzada del anillo.
            respuesta = LlamadaSincronaAws.Ejecutar(
                token => _kms.EncryptAsync(new EncryptRequest
                {
                    KeyId = _keyId,
                    Plaintext = new MemoryStream(textoPlano)
                }, token),
                _limite,
                "El cifrado con KMS de una clave de Data Protection",
                descartarResultadoTardio: null,
                liberarEntrada: null,
                _planificador);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(textoPlano);
        }

        var cifrado = Convert.ToBase64String(respuesta.CiphertextBlob.ToArray());

        return new EncryptedXmlInfo(
            new XElement(NombreElemento, cifrado),
            typeof(KmsXmlDecryptor));
    }
}
