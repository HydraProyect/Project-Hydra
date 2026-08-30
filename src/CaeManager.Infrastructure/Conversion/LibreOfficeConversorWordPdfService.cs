using CaeManager.Application.Common;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Conversion;

/// <summary>
/// Convierte .docx a PDF invocando LibreOffice headless como proceso externo
/// (ver ARCHITECTURE.md, "Archivos" — se descartó Aspose/GroupDocs por su
/// licencia comercial y un servicio cloud por sacar documentos de
/// trabajadores fuera del servidor).
/// </summary>
public class LibreOfficeConversorWordPdfService : IConversorWordPdfService
{
    // Perfil de usuario aislado por conversión (-env:UserInstallation) más un
    // semáforo de instancia única: LibreOffice headless tiene fragilidad
    // documentada con invocaciones concurrentes incluso con perfiles separados
    // (hay estado a nivel de proceso/sistema que esa opción no siempre aísla).
    // Serializar es la opción simple y suficiente para el volumen esperado (uso
    // interno, no SaaS) — no justifica un listener persistente tipo unoconv.
    private static readonly SemaphoreSlim Semaforo = new(1, 1);

    private readonly LibreOfficeConversorWordPdfServiceOptions _opciones;
    private readonly ILogger<LibreOfficeConversorWordPdfService> _logger;

    public LibreOfficeConversorWordPdfService(
        IOptions<LibreOfficeConversorWordPdfServiceOptions> opciones,
        ILogger<LibreOfficeConversorWordPdfService> logger)
    {
        _opciones = opciones.Value;
        _logger = logger;
    }

    public async Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default)
    {
        var directorioTrabajo = Path.Combine(Path.GetTempPath(), $"caemanager-word2pdf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directorioTrabajo);

        try
        {
            var rutaDocx = Path.Combine(directorioTrabajo, "documento.docx");
            await File.WriteAllBytesAsync(rutaDocx, contenidoDocx, cancellationToken);

            var rutaPerfil = Path.Combine(directorioTrabajo, "perfil");

            using var proceso = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _opciones.RutaEjecutable,
                    ArgumentList =
                    {
                        $"-env:UserInstallation=file://{rutaPerfil}",
                        "--headless",
                        "--convert-to", "pdf",
                        "--outdir", directorioTrabajo,
                        rutaDocx,
                    },
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };

            await Semaforo.WaitAsync(cancellationToken);
            try
            {
                try
                {
                    proceso.Start();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
                {
                    throw new InvalidOperationException(
                        $"No se encontró el ejecutable de LibreOffice (\"{_opciones.RutaEjecutable}\"). " +
                        "En despliegue debe estar instalado en la imagen (ver Dockerfile); en desarrollo local, instala LibreOffice o ajusta \"ConversionWordPdf:RutaEjecutable\".", ex);
                }

                // Sin el token del llamador: si se cancela, esta lectura debe
                // poder terminar sola cuando el proceso muera. Atarla a la
                // cancelación dejaría la tarea abandonada sin observar.
                var salidaErrorTask = proceso.StandardError.ReadToEndAsync(CancellationToken.None);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(_opciones.TimeoutSegundos));

                try
                {
                    await proceso.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Se mata SIEMPRE, venga la cancelación del timeout o del
                    // llamador. Antes el filtro `when` excluía el segundo caso:
                    // si el usuario abandonaba el circuito, LibreOffice se
                    // quedaba corriendo indefinidamente con el documento del
                    // trabajador abierto, y el finally de abajo intentaba
                    // borrar el directorio mientras el proceso aún lo tenía
                    // tomado — así que además del proceso huérfano quedaba el
                    // .docx en el temporal y el error de borrado tapaba la
                    // cancelación original.
                    await MatarArbolDeProcesoAsync(proceso);

                    // La cancelación del llamador se propaga como lo que es:
                    // convertirla en un error de aplicación haría que abandonar
                    // una página se registrara como un fallo de conversión.
                    if (cancellationToken.IsCancellationRequested)
                        throw;

                    throw new InvalidOperationException(
                        $"La conversión de Word a PDF con LibreOffice superó el tiempo máximo de {_opciones.TimeoutSegundos} segundos.");
                }

                var rutaPdf = Path.Combine(directorioTrabajo, "documento.pdf");
                if (proceso.ExitCode != 0 || !File.Exists(rutaPdf))
                {
                    var salidaError = await salidaErrorTask;
                    throw new InvalidOperationException(
                        $"LibreOffice no pudo convertir el documento Word a PDF (código de salida {proceso.ExitCode}). {salidaError}");
                }

                return await File.ReadAllBytesAsync(rutaPdf, cancellationToken);
            }
            finally
            {
                Semaforo.Release();
            }
        }
        finally
        {
            BorrarDirectorioDeTrabajo(directorioTrabajo);
        }
    }

    /// <summary>
    /// Mata el árbol del proceso y <b>espera a que muera de verdad</b> antes de
    /// devolver el control. <see cref="Process.Kill(bool)"/> solo pide la
    /// terminación: hasta que el sistema no la completa, el proceso sigue con
    /// los ficheros del directorio de trabajo abiertos, y borrarlo entonces
    /// falla en Windows y deja ficheros a medio soltar en Linux.
    /// </summary>
    private async Task MatarArbolDeProcesoAsync(Process proceso)
    {
        try
        {
            if (proceso.HasExited) return;

            proceso.Kill(entireProcessTree: true);

            using var esperaDeMuerte = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await proceso.WaitForExitAsync(esperaDeMuerte.Token);
        }
        catch (Exception ex)
        {
            // Nunca se deja escapar: llegamos aquí ya cancelando o agotando el
            // tiempo, y sustituir ese motivo por un fallo al matar perdería la
            // causa real.
            _logger.LogWarning(ex,
                "No se pudo terminar el proceso de LibreOffice tras cancelar o agotar el tiempo de conversión. " +
                "Puede haber quedado un proceso huérfano con el documento abierto.");
        }
    }

    private void BorrarDirectorioDeTrabajo(string directorioTrabajo)
    {
        try
        {
            Directory.Delete(directorioTrabajo, recursive: true);
        }
        catch (Exception ex)
        {
            // Mejor esfuerzo: este borrado corre en un finally, así que una
            // excepción aquí sustituiría a la que venía subiendo y ocultaría
            // el motivo real del fallo. Queda constancia porque el directorio
            // contiene una copia del documento del trabajador.
            _logger.LogWarning(ex,
                "No se pudo borrar el directorio temporal de conversión {Directorio}. Contiene una copia del documento subido.",
                directorioTrabajo);
        }
    }
}
