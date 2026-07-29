using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;
using PDFtoImage;

namespace CaeManager.Infrastructure.DocumentosIa;

public class PdfToPngRasterizadorPaginasPdfService : IRasterizadorPaginasPdfService
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
            return Result.Fallo<IReadOnlyList<byte[]>>(
                Error.Crear("Rasterizador.FalloConversion", $"No se pudo rasterizar el PDF: {ex.Message}"));
        }
    }
}
