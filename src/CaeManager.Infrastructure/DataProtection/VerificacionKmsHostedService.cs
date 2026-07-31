using System.Text;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.DataProtection;

/// <summary>
/// Comprueba al arrancar que la clave de KMS configurada existe, es accesible
/// y hace el viaje completo de ida y vuelta.
///
/// Sin esto, una credencial mal copiada o una política IAM sin
/// <c>kms:Decrypt</c> no se nota hasta que Data Protection intenta usar la
/// clave: al crear la siguiente (hasta 90 días después) o al abrir una
/// credencial de portal externo guardada. Es decir, se notaría tarde y en la
/// cara de un usuario, no en el despliegue.
///
/// No tumba el arranque a propósito. Si KMS estuviera caído un momento,
/// negarse a arrancar convertiría una incidencia pasajera de AWS en una caída
/// del producto; y con la configuración mal puesta la aplicación va a fallar
/// igualmente al primer uso real de la clave, así que adelantar el fallo no
/// añade seguridad, solo ruido. Lo que sí añade es el log: queda dicho, en el
/// arranque, si el cifrado está operativo o no.
/// </summary>
public class VerificacionKmsHostedService(
    IAmazonKeyManagementService kms,
    IOptions<DataProtectionKmsOptions> opciones,
    ILogger<VerificacionKmsHostedService> logger) : IHostedService
{
    private const string Testigo = "verificacion-kms-caemanager";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var keyId = opciones.Value.KeyId!;

        try
        {
            var cifrado = await kms.EncryptAsync(
                new EncryptRequest
                {
                    KeyId = keyId,
                    Plaintext = new MemoryStream(Encoding.UTF8.GetBytes(Testigo))
                },
                cancellationToken);

            var descifrado = await kms.DecryptAsync(
                new DecryptRequest { CiphertextBlob = cifrado.CiphertextBlob },
                cancellationToken);

            if (Encoding.UTF8.GetString(descifrado.Plaintext.ToArray()) != Testigo)
            {
                logger.LogError(
                    "Data Protection: KMS respondió pero el texto descifrado no coincide con el original " +
                    "(clave {KeyId}). NO confíes en el cifrado de las claves hasta aclararlo.", keyId);
                return;
            }

            logger.LogInformation(
                "Data Protection: cifrado con AWS KMS operativo (clave {KeyId}, región {Region}). " +
                "Las claves creadas a partir de ahora se guardan cifradas; las anteriores siguen en claro.",
                keyId, opciones.Value.Region);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Data Protection: KMS está activado pero la clave {KeyId} no responde. Las claves nuevas " +
                "NO se podrán cifrar y, si ya hay alguna cifrada, las credenciales guardadas no se podrán " +
                "leer. Revisa DataProtection:Kms (credenciales, región y permisos kms:Encrypt/kms:Decrypt).",
                keyId);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
