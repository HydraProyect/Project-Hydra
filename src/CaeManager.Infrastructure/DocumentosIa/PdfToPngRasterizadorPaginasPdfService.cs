using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using PDFtoImage;

namespace CaeManager.Infrastructure.DocumentosIa;

public class PdfToPngRasterizadorPaginasPdfService(
    ILogger<PdfToPngRasterizadorPaginasPdfService> logger) : IRasterizadorPaginasPdfService
{
    private static readonly RenderOptions OpcionesRender = new(Dpi: 150);

    public Result<IReadOnlyList<byte[]>> RasterizarPaginas(byte[] contenidoPdf, IReadOnlyList<int> indicesPaginas)
    {
        if (indicesPaginas.Count == 0)
            return Result.Exito<IReadOnlyList<byte[]>>(Array.Empty<byte[]>());

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return Result.Fallo<IReadOnlyList<byte[]>>(
                Error.Crear("Rasterizador.PlataformaNoSoportada", "La rasterización de PDF no está soportada en esta plataforma."));

        try
        {
            var resultado = new List<byte[]>(indicesPaginas.Count);
            foreach (var indice in indicesPaginas)
            {
                using var ms = new MemoryStream();
                global::PDFtoImage.Conversion.SavePng(ms, contenidoPdf, new Index(indice), password: null, OpcionesRender);
                resultado.Add(ms.ToArray());
            }
            return Result.Exito<IReadOnlyList<byte[]>>(resultado);
        }
        catch (Exception ex)
        {
            // La excepción va al log, no al mensaje del Result. Ese mensaje no
            // se queda en pantalla: DocumentAIRouterService lo copia a las
            // Incidencias de la auditoría, y cuando el trabajo falla acaba
            // tambien en TrabajoAnalisisDocumento.UltimoError y en las migas de
            // pan de Sentry — tres destinos persistentes, con sus backups, para
            // un texto que produce una librería de terceros al tropezar con el
            // PDF de un cliente. Mismo criterio que el resto del directorio
            // (ver PdfSharpClasificadorDocumentoService) y que
            // CorrelacionRespuestaIa para las respuestas de proveedor.
            logger.LogError(ex, "No se pudo rasterizar el PDF ({Paginas} páginas solicitadas).", indicesPaginas.Count);

            return Result.Fallo<IReadOnlyList<byte[]>>(
                Error.Crear("Rasterizador.FalloConversion", "No pudimos convertir las páginas de este documento."));
        }
    }
}
