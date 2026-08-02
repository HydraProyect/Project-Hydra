using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.DataProtection;

/// <summary>
/// Comprueba al arrancar que el bucket de <c>DataProtection:S3</c> configurado
/// existe, es accesible y hace el viaje completo de ida y vuelta (put/get/
/// delete) — mismo criterio que <c>VerificacionKmsHostedService</c>/
/// <c>VerificacionAlmacenamientoS3HostedService</c>: una credencial IAM mal
/// copiada no se nota hasta que una réplica necesita descifrar algo que
/// cifró otra, y ahí ya es tarde.
/// </summary>
public class VerificacionDataProtectionS3HostedService(
    IOptions<DataProtectionS3Options> opciones,
    ILogger<VerificacionDataProtectionS3HostedService> logger) : IHostedService
{
    private const string NombreTestigo = "_verificacion-dataprotection-s3-caemanager";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = opciones.Value;
        using var cliente = new AmazonS3Client(
            config.AccessKeyId, config.SecretAccessKey, RegionEndpoint.GetBySystemName(config.Region));
        var clave = $"{config.Prefijo}{NombreTestigo}";

        try
        {
            await cliente.PutObjectAsync(new PutObjectRequest
            {
                BucketName = config.BucketName,
                Key = clave,
                ContentBody = DateTime.UtcNow.ToString("O"),
            }, cancellationToken);

            await cliente.GetObjectMetadataAsync(config.BucketName, clave, cancellationToken);
            await cliente.DeleteObjectAsync(config.BucketName, clave, cancellationToken);

            logger.LogInformation(
                "Llavero de Data Protection: S3 operativo (bucket {Bucket}, región {Region}, prefijo {Prefijo}). " +
                "Todas las réplicas comparten el mismo juego de claves.",
                config.BucketName, config.Region, config.Prefijo);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Llavero de Data Protection: DataProtection:S3 está activado pero el bucket {Bucket} no " +
                "respondió al put/get/delete de prueba. Revisa AccessKeyId/SecretAccessKey/BucketName/Region " +
                "y los permisos IAM — mientras tanto, las claves siguen leyéndose/escribiéndose ahí igual, " +
                "así que cualquier fallo real de acceso solo aparecerá al generar o leer una clave.",
                config.BucketName);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
