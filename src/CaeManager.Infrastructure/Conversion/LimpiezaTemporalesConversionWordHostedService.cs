using CaeManager.Application.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Conversion;

/// <summary>
/// Barrido al arrancar de los directorios temporales de la conversión de Word a
/// PDF (ASVS V8.3.6): si el proceso cayó a mitad de una conversión, el
/// <c>finally</c> de <see cref="LibreOfficeConversorWordPdfService"/> no llegó a
/// correr y el directorio conserva una copia del documento del trabajador.
/// Borra los de más de una hora que ninguna conversión viva tiene tomados.
/// Mira también el temporal global del sistema, donde la versión anterior del
/// servicio dejaba sus restos.
/// </summary>
public class LimpiezaTemporalesConversionWordHostedService(
    IOptions<LibreOfficeConversorWordPdfServiceOptions> opciones,
    ILogger<LimpiezaTemporalesConversionWordHostedService> logger) : IHostedService
{
    private static readonly TimeSpan Antiguedad = TimeSpan.FromHours(1);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // P41c: lo que haga este servicio de fondo lo hace la propia plataforma.
        // No escribe entidades auditadas, pero se declara igual en el punto de
        // entrada: una excepción "porque este no escribe" caducaría el día que
        // escribiera.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        var raices = new[] { opciones.Value.RutaTemporales, Path.GetTempPath() }
            .Select(r => Path.GetFullPath(r))
            .Distinct(StringComparer.Ordinal);
        foreach (var raiz in raices)
            BarrerRaiz(raiz, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void BarrerRaiz(string raiz, CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(raiz)) return;

            foreach (var directorio in Directory.EnumerateDirectories(raiz, LibreOfficeConversorWordPdfService.PrefijoDirectorio + "*"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new DirectoryInfo(directorio);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        info.LastWriteTimeUtc >= DateTime.UtcNow - Antiguedad) continue;

                    // Otra instancia puede seguir convirtiendo en este directorio:
                    // una conversión viva mantiene .activo abierto en exclusiva y
                    // aquí la apertura falla. Ninguna conversión nueva reutiliza
                    // un directorio aleatorio ya existente.
                    using (new FileStream(Path.Combine(directorio, LibreOfficeConversorWordPdfService.NombreMarcaActivo),
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }

                    Directory.Delete(directorio, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex,
                        "No se pudo borrar el directorio temporal de conversión {Directorio}. Puede contener una copia del documento subido.",
                        directorio);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "No se pudieron revisar los temporales de conversión de Word en {Raiz}.", raiz);
        }
    }
}
