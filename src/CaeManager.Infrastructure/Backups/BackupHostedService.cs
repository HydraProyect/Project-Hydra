using System.IO.Compression;
using Amazon;
using Amazon.S3;
using Amazon.S3.Transfer;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Backups;

/// <summary>
/// Backup periódico de CaeManager.db + las claves de Data Protection a S3,
/// subidos siempre juntos como una unidad (ver RUNBOOK-CLAVES.md — restaurar
/// solo la base de datos con un juego de claves distinto deja las
/// credenciales de Empresa/Subcontrata cifradas permanentemente
/// irrecuperables). Sin `Backups:Activo`, no hace nada — mismo patrón que
/// DatosPruebaSeeder para no intentar tocar AWS sin cuenta provisionada.
///
/// El backup de la base de datos usa `SqliteConnection.BackupDatabase`, el
/// mecanismo online de SQLite (equivalente a `sqlite3 .backup`) — no bloquea
/// las escrituras de la app mientras se genera, a diferencia de copiar el
/// archivo .db directamente mientras está abierto.
/// </summary>
public class BackupHostedService(
    IConfiguration configuration,
    IHostEnvironment entorno,
    IOptions<BackupsOptions> opciones,
    ILogger<BackupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = opciones.Value;

        if (!config.Activo)
        {
            logger.LogInformation("Backups:Activo no está activado — el servicio de backups automáticos no arranca.");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.Aws.AccessKeyId) || string.IsNullOrWhiteSpace(config.Aws.SecretAccessKey)
            || string.IsNullOrWhiteSpace(config.Aws.BucketName) || string.IsNullOrWhiteSpace(config.Aws.Region))
        {
            logger.LogWarning("Backups:Activo está en true pero faltan variables de AWS (AccessKeyId/SecretAccessKey/BucketName/Region) — el servicio de backups no arranca.");
            return;
        }

        // Identifica de qué servicio de Railway viene cada backup (producción
        // vs. staging comparten el mismo bucket) — RAILWAY_SERVICE_NAME lo
        // inyecta Railway automáticamente; en local (sin esa variable) se
        // usa el nombre del entorno para no mezclar backups de desarrollo
        // con los de un despliegue real.
        var nombreServicio = Environment.GetEnvironmentVariable("RAILWAY_SERVICE_NAME") ?? $"local-{entorno.EnvironmentName}";

        logger.LogInformation(
            "Servicio de backups automáticos activo: cada {IntervaloHoras}h a s3://{Bucket}/{Servicio}/...",
            config.IntervaloHoras, config.Aws.BucketName, nombreServicio);

        using var periodicTimer = new PeriodicTimer(TimeSpan.FromHours(config.IntervaloHoras));

        // Un primer backup al arrancar, no solo tras el primer intervalo —
        // así un redeploy no deja el sistema sin ningún backup reciente
        // durante horas si el proceso se reinicia a menudo.
        await EjecutarBackupAsync(config, nombreServicio, stoppingToken);

        while (await periodicTimer.WaitForNextTickAsync(stoppingToken))
        {
            await EjecutarBackupAsync(config, nombreServicio, stoppingToken);
        }
    }

    private async Task EjecutarBackupAsync(BackupsOptions config, string nombreServicio, CancellationToken cancellationToken)
    {
        var directorioTemporal = Path.Combine(Path.GetTempPath(), $"backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directorioTemporal);

        try
        {
            var rutaBackupDb = Path.Combine(directorioTemporal, "CaeManager.db");
            var rutaZipClaves = Path.Combine(directorioTemporal, "dataprotection-keys.zip");

            RespaldarBaseDeDatos(rutaBackupDb);
            RespaldarClavesDataProtection(rutaZipClaves);

            var marcaTiempo = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var prefijo = $"{nombreServicio}/{marcaTiempo}";

            var regionEndpoint = RegionEndpoint.GetBySystemName(config.Aws.Region);
            using var clienteS3 = new AmazonS3Client(config.Aws.AccessKeyId, config.Aws.SecretAccessKey, regionEndpoint);
            using var transferencia = new TransferUtility(clienteS3);

            await transferencia.UploadAsync(rutaBackupDb, config.Aws.BucketName, $"{prefijo}/CaeManager.db", cancellationToken);
            await transferencia.UploadAsync(rutaZipClaves, config.Aws.BucketName, $"{prefijo}/dataprotection-keys.zip", cancellationToken);

            logger.LogInformation("Backup subido correctamente a s3://{Bucket}/{Prefijo}/", config.Aws.BucketName, prefijo);
        }
        catch (Exception ex)
        {
            // Un backup fallido no debe tumbar la app — se registra y se
            // reintenta en el próximo intervalo.
            logger.LogError(ex, "Falló el backup automático a S3.");
        }
        finally
        {
            Directory.Delete(directorioTemporal, recursive: true);
        }
    }

    private void RespaldarBaseDeDatos(string rutaDestino)
    {
        var connectionStringBuilder = new SqliteConnectionStringBuilder(configuration.GetConnectionString("CaeManagerDb"));

        using var origen = new SqliteConnection(connectionStringBuilder.ConnectionString);
        using var destino = new SqliteConnection($"Data Source={rutaDestino}");

        origen.Open();
        destino.Open();
        origen.BackupDatabase(destino);
    }

    private void RespaldarClavesDataProtection(string rutaZipDestino)
    {
        var rutaClaves = configuration["DataProtection:RutaClaves"] ?? "App_Data/dataprotection-keys";
        var rutaClavesAbsoluta = Path.IsPathRooted(rutaClaves)
            ? rutaClaves
            : Path.Combine(entorno.ContentRootPath, rutaClaves);

        if (!Directory.Exists(rutaClavesAbsoluta) || Directory.GetFiles(rutaClavesAbsoluta).Length == 0)
        {
            // Arranque en frío sin ninguna clave generada todavía (poco
            // probable en producción, pero posible justo tras el primer
            // despliegue) — se crea un zip vacío en vez de fallar el backup
            // entero por esto.
            using (ZipFile.Open(rutaZipDestino, ZipArchiveMode.Create)) { }
            return;
        }

        ZipFile.CreateFromDirectory(rutaClavesAbsoluta, rutaZipDestino);
    }
}
