namespace CaeManager.Infrastructure.FileStorage;

/// <summary>
/// Almacenamiento de los PDFs de Documento en S3 en vez de disco local (P2
/// #22 de docs/business/MATURITY_REVIEW.md — desbloquea multi-réplica: en
/// disco local, un archivo subido a una réplica no lo ven las demás, porque
/// cada una tiene su propio volumen).
///
/// Apagado por defecto, mismo patrón que <c>Backups:Activo</c>/
/// <c>DataProtection:Kms:Activo</c>: sin cuenta de AWS provisionada, la app
/// sigue usando <c>DiskFileStorageService</c> sin más. Credenciales propias,
/// separadas de las de Backups y de las de KMS (ver DEPLOY.md) — que se
/// filtren unas no debe dar acceso a lo que protegen las otras.
/// </summary>
public class AlmacenamientoS3Options
{
    public const string SeccionConfiguracion = "AlmacenamientoS3";

    public bool Activo { get; set; }

    public string? AccessKeyId { get; set; }

    public string? SecretAccessKey { get; set; }

    public string? BucketName { get; set; }

    public string? Region { get; set; }

    public bool EstaConfigurado =>
        Activo &&
        !string.IsNullOrWhiteSpace(AccessKeyId) &&
        !string.IsNullOrWhiteSpace(SecretAccessKey) &&
        !string.IsNullOrWhiteSpace(BucketName) &&
        !string.IsNullOrWhiteSpace(Region);
}
