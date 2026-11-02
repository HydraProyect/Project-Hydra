using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using PDFtoImage;

namespace CaeManager.Infrastructure.DocumentosIa;

public class PdfToPngRasterizadorPaginasPdfService(
    ILogger<PdfToPngRasterizadorPaginasPdfService> logger) : IRasterizadorPaginasPdfService
{
    private const int PuntosPorPulgada = 72;
    private const int Dpi = 150;

    /// <summary>
    /// Tope del bitmap BGRA (4 bytes por píxel) que se pide a la librería nativa
    /// para una página: 64 MiB. Una página A4 a 150 ppp ocupa ~9 MiB y un A2
    /// ~33 MiB; un A1 ya queda fuera. El PDF no limita el tamaño de su propia
    /// página: medido con PDFtoImage 5.2.1, un PDF de 333 bytes con una página
    /// de 139 pulgadas reservaba 1,7 GiB y tardaba ~30 s de CPU en el mismo
    /// proceso que sirve Blazor (ASVS 4.0.3 V12.1.1, API4). Hay que leer las
    /// dimensiones y rechazar antes de rasterizar, porque la librería solo falla
    /// cuando ya no puede reservar (a las 200 pulgadas), y mucho después de
    /// haber dañado al resto de sesiones.
    /// </summary>
    internal const long MaximoBytesBitmapPagina = 64L * 1024 * 1024;

    private const int BytesPorPixel = 4;

    private static readonly RenderOptions OpcionesRender = new(Dpi: Dpi);

    public Result<byte[]> RasterizarPagina(byte[] contenidoPdf, int indicePagina, CancellationToken cancellationToken = default) =>
        Rasterizar(contenidoPdf, indicePagina, OpcionesRender, anchoPixeles: null, cancellationToken);

    public Result<byte[]> RasterizarMiniatura(byte[] contenidoPdf, int indicePagina, int anchoPixeles, CancellationToken cancellationToken = default) =>
        Rasterizar(contenidoPdf, indicePagina, new RenderOptions(Dpi: Dpi, Width: anchoPixeles, WithAspectRatio: true), anchoPixeles, cancellationToken);

    /// <summary>
    /// Bytes del bitmap que se pediría para la página: a 150 ppp, o, en una
    /// miniatura, de <paramref name="anchoPixeles"/> de ancho con el alto que dé
    /// la proporción (una página de 10 x 14000 pt a 48 px de ancho pide
    /// 48 x 67200 px, así que la miniatura tampoco está a salvo). Un tamaño no
    /// finito o no positivo se trata como desmesurado.
    /// </summary>
    private static double BytesDelBitmap(double anchoPuntos, double altoPuntos, int? anchoPixeles)
    {
        if (!double.IsFinite(anchoPuntos) || !double.IsFinite(altoPuntos) || anchoPuntos <= 0 || altoPuntos <= 0)
            return double.PositiveInfinity;

        if (anchoPixeles is { } ancho)
            return (double)ancho * (ancho * altoPuntos / anchoPuntos) * BytesPorPixel;

        var factor = (double)Dpi / PuntosPorPulgada;
        return anchoPuntos * factor * (altoPuntos * factor) * BytesPorPixel;
    }

    private Result<byte[]> Rasterizar(byte[] contenidoPdf, int indicePagina, RenderOptions opciones, int? anchoPixeles, CancellationToken cancellationToken)
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
            var tamano = global::PDFtoImage.Conversion.GetPageSize(contenidoPdf, new Index(indicePagina), password: null);
            var bytesBitmap = BytesDelBitmap(tamano.Width, tamano.Height, anchoPixeles);
            if (bytesBitmap > MaximoBytesBitmapPagina)
            {
                logger.LogWarning(
                    "Se rechaza la página {Pagina} del PDF: mide {Ancho} x {Alto} pt y pediría un bitmap de más de {TopeMiB} MiB.",
                    indicePagina, tamano.Width, tamano.Height, MaximoBytesBitmapPagina / (1024 * 1024));

                return Result.Fallo<byte[]>(
                    Error.Crear("Rasterizador.PaginaDemasiadoGrande", "Una página de este documento es demasiado grande para convertirla."));
            }

            using var ms = new MemoryStream();
            global::PDFtoImage.Conversion.SavePng(ms, contenidoPdf, new Index(indicePagina), password: null, opciones);
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
