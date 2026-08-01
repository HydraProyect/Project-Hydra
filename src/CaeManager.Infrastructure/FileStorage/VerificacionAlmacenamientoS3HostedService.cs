using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.FileStorage;

/// <summary>
/// Comprueba al arrancar que el bucket de <c>AlmacenamientoS3</c> configurado
/// existe, es accesible y hace el viaje completo de ida y vuelta (put/get/
/// delete) — mismo criterio que <c>VerificacionKmsHostedService</c> (Data
/// Protection): una credencial IAM mal copiada o sin permiso de escritura no
/// se nota hasta que un usuario real sube un Documento, y ahí ya es tarde.
///
/// No tumba el arranque a propósito, mismo motivo que la de KMS: un fallo
/// pasajero de S3 no debe convertirse en una caída del producto entero, y con
/// la configuración mal puesta va a fallar igual en el primer uso real — lo
/// que aporta este servicio es que quede dicho en el log del despliegue, no
/// en la cara del primer usuario que suba un archivo.
/// </summary>
public class VerificacionAlmacenamientoS3HostedService(
    IOptions<AlmacenamientoS3Options> opciones,
    ILogger<VerificacionAlmacenamientoS3HostedService> logger) : IHostedService
{
    private const string ClaveTestigo = "_verificacion-almacenamiento-s3-caemanager.txt";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = opciones.Value;
        using var cliente = new AmazonS3Client(
            config.AccessKeyId, config.SecretAccessKey, RegionEndpoint.GetBySystemName(config.Region));

        try
        {
            await cliente.PutObjectAsync(new PutObjectRequest
            {
                BucketName = config.BucketName,
                Key = ClaveTestigo,
                ContentBody = DateTime.UtcNow.ToString("O"),
            }, cancellationToken);

            await cliente.GetObjectMetadataAsync(config.BucketName, ClaveTestigo, cancellationToken);
            await cliente.DeleteObjectAsync(config.BucketName, ClaveTestigo, cancellationToken);

            logger.LogInformation(
                "Almacenamiento de Documentos: S3 operativo (bucket {Bucket}, región {Region}). " +
                "Los archivos nuevos se guardan ahí en vez de en el disco local.",
                config.BucketName, config.Region);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Almacenamiento de Documentos: AlmacenamientoS3 está activado pero el bucket {Bucket} no " +
                "respondió al put/get/delete de prueba. Revisa AccessKeyId/SecretAccessKey/BucketName/Region " +
                "y los permisos IAM — mientras tanto, cualquier subida de Documento va a fallar.",
                config.BucketName);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
