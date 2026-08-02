namespace CaeManager.Infrastructure.DataProtection;

/// <summary>
/// Llavero de Data Protection compartido en S3 en vez de en el disco local de
/// cada réplica (P3-30 de docs/business/MATURITY_REVIEW.md — junto con el
/// backplane de SignalR y la elección de líder de los <c>BackgroundService</c>,
/// la última pieza que ataba la app a una sola réplica: en disco local, cada
/// réplica genera/lee su propio juego de claves, así que una cookie o
/// credencial cifrada por una réplica no la puede descifrar otra).
///
/// Apagado por defecto, mismo patrón que <c>AlmacenamientoS3</c>/
/// <c>DataProtection:Kms</c>: sin cuenta de AWS provisionada, las claves
/// siguen en <c>PersistKeysToFileSystem</c> (ruta local), que es lo correcto
/// para un despliegue de una sola réplica. Credenciales propias, separadas de
/// las de Backups/KMS/AlmacenamientoS3 (ver DEPLOY.md).
/// </summary>
public class DataProtectionS3Options
{
    public const string SeccionConfiguracion = "DataProtection:S3";

    public bool Activo { get; set; }

    public string? AccessKeyId { get; set; }

    public string? SecretAccessKey { get; set; }

    public string? BucketName { get; set; }

    public string? Region { get; set; }

    /// <summary>Prefijo de objeto dentro del bucket — permite compartir bucket con AlmacenamientoS3 sin mezclar claves y Documentos.</summary>
    public string Prefijo { get; set; } = "dataprotection-keys/";

    public bool EstaConfigurado =>
        Activo &&
        !string.IsNullOrWhiteSpace(AccessKeyId) &&
        !string.IsNullOrWhiteSpace(SecretAccessKey) &&
        !string.IsNullOrWhiteSpace(BucketName) &&
        !string.IsNullOrWhiteSpace(Region);
}
