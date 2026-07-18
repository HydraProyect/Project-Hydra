namespace CaeManager.Infrastructure.Backups;

/// <summary>
/// Configuración del backup automático de CaeManager.db + las claves de
/// Data Protection hacia S3 — ver RUNBOOK-CLAVES.md para por qué ambos se
/// suben siempre juntos, nunca por separado. Apagado por defecto (mismo
/// patrón que DatosPrueba:Activo): sin cuenta de AWS provisionada, el
/// servicio no debe intentar nada.
/// </summary>
public class BackupsOptions
{
    public const string SeccionConfiguracion = "Backups";

    public bool Activo { get; set; }

    public int IntervaloHoras { get; set; } = 24;

    public AwsOptions Aws { get; set; } = new();

    public class AwsOptions
    {
        public string? AccessKeyId { get; set; }
        public string? SecretAccessKey { get; set; }
        public string? BucketName { get; set; }
        public string? Region { get; set; }
    }
}
