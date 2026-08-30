using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using PDFtoImage;

namespace CaeManager.Infrastructure.DocumentosIa;

public class PdfToPngRasterizadorPaginasPdfService(
    ILogger<PdfToPngRasterizadorPaginasPdfService> logger) : IRasterizadorPaginasPdfService
{
    private static readonly RenderOptions OpcionesRender = new(Dpi: 150);

    public Result<byte[]> RasterizarPagina(byte[] contenidoPdf, int indicePagina, CancellationToken cancellationToken = default)
    {
        // Antes de abrir nada: si ya se pidió parar, la página siguiente no
        // empieza. La conversión en sí es nativa y síncrona, así que este es el
        // único punto donde la cancelación puede llegar a tiempo.
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return Result.Fallo<byte[]>(
                Error.Crear("Rasterizador.PlataformaNoSoportada", "La rasterización de PDF no está soportada en esta plataforma."));

        try
        {
            using var ms = new MemoryStream();
            global::PDFtoImage.Conversion.SavePng(ms, contenidoPdf, new Index(indicePagina), password: null, OpcionesRender);
            return Result.Exito(ms.ToArray());
        }
        catch (Exception ex)
        {
            // La excepción va al log, no al mensaje del Result. Ese mensaje no
            // se queda en pantalla: DocumentAIRouterService lo copia a las
            // Incidencias de la auditoría y, cuando el trabajo falla, acaba
            // también en TrabajoAnalisisDocumento.UltimoError y en las migas de
            // pan de Sentry — tres destinos persistentes, con sus backups, para
            // un texto que produce una librería de terceros al tropezar con el
            // PDF de un cliente. Mismo criterio que el resto del directorio
            // (ver PdfSharpClasificadorDocumentoService) y que
            // CorrelacionRespuestaIa para las respuestas de proveedor.
            logger.LogError(ex, "No se pudo rasterizar la página {Pagina} del PDF.", indicePagina);

            return Result.Fallo<byte[]>(
                Error.Crear("Rasterizador.FalloConversion", "No pudimos convertir las páginas de este documento."));
        }
    }
}
