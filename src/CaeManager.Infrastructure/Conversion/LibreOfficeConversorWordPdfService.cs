using System.Diagnostics;
using CaeManager.Application.Common;
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

    public LibreOfficeConversorWordPdfService(IOptions<LibreOfficeConversorWordPdfServiceOptions> opciones)
    {
        _opciones = opciones.Value;
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

                var salidaErrorTask = proceso.StandardError.ReadToEndAsync(cancellationToken);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(_opciones.TimeoutSegundos));

                try
                {
                    await proceso.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    proceso.Kill(entireProcessTree: true);
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
            Directory.Delete(directorioTrabajo, recursive: true);
        }
    }
}
