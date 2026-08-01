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

    /// <summary>
    /// Binario de pg_dump cuando el motor es PostgreSQL. En el contenedor de
    /// despliegue está en el PATH (ver Dockerfile); en desarrollo Windows
    /// suele hacer falta la ruta completa (p. ej.
    /// "C:\Program Files\PostgreSQL\17\bin\pg_dump.exe").
    /// </summary>
    public string RutaPgDump { get; set; } = "pg_dump";

    public AwsOptions Aws { get; set; } = new();

    public class AwsOptions
    {
        public string? AccessKeyId { get; set; }
        public string? SecretAccessKey { get; set; }
        public string? BucketName { get; set; }
        public string? Region { get; set; }
    }
}
