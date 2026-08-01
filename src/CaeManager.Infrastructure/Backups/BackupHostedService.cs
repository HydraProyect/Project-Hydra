using System.Diagnostics;
using System.IO.Compression;
using Amazon;
using Amazon.S3;
using Amazon.S3.Transfer;
using CaeManager.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CaeManager.Infrastructure.Backups;

/// <summary>
/// Backup periódico de la base de datos + las claves de Data Protection a S3,
/// subidos siempre juntos como una unidad (ver RUNBOOK-CLAVES.md — restaurar
/// solo la base de datos con un juego de claves distinto deja las
/// credenciales de Empresa/Subcontrata cifradas permanentemente
/// irrecuperables). Sin `Backups:Activo`, no hace nada — mismo patrón que
/// DatosPruebaSeeder para no intentar tocar AWS sin cuenta provisionada.
///
/// El mecanismo depende del motor (`Database:Proveedor`, ver
/// ProveedorBaseDatos): con SQLite usa `SqliteConnection.BackupDatabase`, el
/// mecanismo online de SQLite (no bloquea las escrituras de la app, a
/// diferencia de copiar el archivo .db abierto); con PostgreSQL invoca
/// `pg_dump --format=custom` (instalado en la imagen de despliegue, ver
/// Dockerfile), que igualmente no bloquea al servidor y produce un archivo
/// restaurable con `pg_restore`.
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
            var rutaZipClaves = Path.Combine(directorioTemporal, "dataprotection-keys.zip");

            var rutaBackupDb = await RespaldarBaseDeDatosAsync(directorioTemporal, cancellationToken);
            RespaldarClavesDataProtection(rutaZipClaves);

            var marcaTiempo = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var prefijo = $"{nombreServicio}/{marcaTiempo}";

            var regionEndpoint = RegionEndpoint.GetBySystemName(config.Aws.Region);
            using var clienteS3 = new AmazonS3Client(config.Aws.AccessKeyId, config.Aws.SecretAccessKey, regionEndpoint);
            using var transferencia = new TransferUtility(clienteS3);

            await transferencia.UploadAsync(rutaBackupDb, config.Aws.BucketName, $"{prefijo}/{Path.GetFileName(rutaBackupDb)}", cancellationToken);
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

    private async Task<string> RespaldarBaseDeDatosAsync(string directorioTemporal, CancellationToken cancellationToken)
    {
        var cadenaConexion = configuration.GetConnectionString("CaeManagerDb")
            ?? throw new InvalidOperationException("Falta el connection string CaeManagerDb.");

        if (LectorProveedorBaseDatos.Leer(configuration) == ProveedorBaseDatos.PostgreSql)
        {
            var rutaDump = Path.Combine(directorioTemporal, "CaeManager.dump");
            await EjecutarPgDumpAsync(cadenaConexion, rutaDump, cancellationToken);
            return rutaDump;
        }

        var rutaDb = Path.Combine(directorioTemporal, "CaeManager.db");
        RespaldarSqlite(cadenaConexion, rutaDb);
        return rutaDb;
    }

    private static void RespaldarSqlite(string cadenaConexion, string rutaDestino)
    {
        var connectionStringBuilder = new SqliteConnectionStringBuilder(cadenaConexion);

        using var origen = new SqliteConnection(connectionStringBuilder.ConnectionString);
        using var destino = new SqliteConnection($"Data Source={rutaDestino}");

        origen.Open();
        destino.Open();
        origen.BackupDatabase(destino);
    }

    private async Task EjecutarPgDumpAsync(string cadenaConexion, string rutaDestino, CancellationToken cancellationToken)
    {
        var conexion = new NpgsqlConnectionStringBuilder(cadenaConexion);

        var inicio = new ProcessStartInfo
        {
            FileName = opciones.Value.RutaPgDump,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        inicio.ArgumentList.Add($"--host={conexion.Host}");
        inicio.ArgumentList.Add($"--port={conexion.Port}");
        inicio.ArgumentList.Add($"--username={conexion.Username}");
        inicio.ArgumentList.Add($"--dbname={conexion.Database}");
        // Formato custom: comprimido y restaurable con pg_restore (que además
        // permite restaurar tablas sueltas si hiciera falta), a diferencia
        // del volcado SQL plano.
        inicio.ArgumentList.Add("--format=custom");
        inicio.ArgumentList.Add($"--file={rutaDestino}");
        // Por variable de entorno del proceso hijo, nunca como argumento: la
        // línea de comandos es visible para cualquier proceso de la máquina.
        inicio.Environment["PGPASSWORD"] = conexion.Password;

        using var proceso = Process.Start(inicio)
            ?? throw new InvalidOperationException($"No se pudo iniciar pg_dump ('{opciones.Value.RutaPgDump}').");

        var errores = await proceso.StandardError.ReadToEndAsync(cancellationToken);
        await proceso.WaitForExitAsync(cancellationToken);

        if (proceso.ExitCode != 0)
            throw new InvalidOperationException($"pg_dump terminó con código {proceso.ExitCode}: {errores.Trim()}");
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
