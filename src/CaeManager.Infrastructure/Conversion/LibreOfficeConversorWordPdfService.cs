using CaeManager.Application.Common;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Conversion;

/// <summary>
/// Convierte .docx a PDF invocando LibreOffice headless como proceso externo
/// (ver Project-Hydra-Negocio/tecnico/ARCHITECTURE.md, "Archivos" — se descartó Aspose/GroupDocs por su
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
    //
    // La espera por el semáforo también tiene tope (EsperaMaximaSegundos): el
    // TimeoutSegundos solo cubre el proceso, así que sin él una conversión atascada
    // en cola dejaba esperando sin límite a todas las demás cargas de Word.
    // internal para que los tests puedan ocuparlo sin lanzar LibreOffice.
    internal static readonly SemaphoreSlim Semaforo = new(1, 1);

    // Los comparte el barrido de arranque (LimpiezaTemporalesConversionWordHostedService).
    internal const string PrefijoDirectorio = "caemanager-word2pdf-";
    internal const string NombreMarcaActivo = ".activo";

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
        var directorioTrabajo = Path.Combine(_opciones.RutaTemporales, $"{PrefijoDirectorio}{Guid.NewGuid():N}");
        CrearDirectorioPrivado(directorioTrabajo);

        try
        {
            // Marca de conversión viva: abierta en exclusiva hasta que termina la
            // conversión, para que el barrido de arranque de otra instancia no
            // borre un directorio que sigue en uso.
            using var bloqueo = new FileStream(Path.Combine(directorioTrabajo, NombreMarcaActivo), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None);
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
            proceso.StartInfo.Environment["TMPDIR"] = directorioTrabajo;
            proceso.StartInfo.Environment["TMP"] = directorioTrabajo;
            proceso.StartInfo.Environment["TEMP"] = directorioTrabajo;

            if (!await Semaforo.WaitAsync(TimeSpan.FromSeconds(_opciones.EsperaMaximaSegundos), cancellationToken))
            {
                _logger.LogWarning(
                    "Conversión de Word a PDF rechazada: el servicio siguió ocupado durante {Segundos} s.",
                    _opciones.EsperaMaximaSegundos);
                throw new ConversionWordPdfOcupadaException();
            }

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

    private static void CrearDirectorioPrivado(string ruta)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        if (OperatingSystem.IsWindows())
        {
            using var identidad = WindowsIdentity.GetCurrent();
            var usuario = identidad.User ?? throw new InvalidOperationException("No se pudo identificar al usuario del proceso.");
            var seguridad = new DirectorySecurity();
            seguridad.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            seguridad.AddAccessRule(new FileSystemAccessRule(usuario, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(ruta).Create(seguridad);
        }
        else
        {
            Directory.CreateDirectory(ruta, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
