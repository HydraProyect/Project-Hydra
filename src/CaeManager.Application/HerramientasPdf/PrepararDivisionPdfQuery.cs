using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Extraccion;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>
/// Lee un PDF para dividirlo y devuelve sugerencias, sin escribir nada: número de páginas, si lleva firma, los tramos
/// detectados y el catálogo con el que se revisan. La lectura es solo local: este handler no conoce ningún puerto de
/// IA, y un test de arquitectura lo mantiene así (ver <see cref="DetectorTramosPdf"/>).
/// </summary>
public record PrepararDivisionPdfQuery(byte[] ContenidoPdf, Guid EmpresaGestionadaId) : IRequest<Result<PreparacionDivisionPdf>>;

/// <param name="TextoLegible">
/// <c>false</c> si se pudo abrir pero no leer el texto: se puede dividir a mano, sin sugerencias.
/// </param>
public record PreparacionDivisionPdf(
    int NumeroPaginas, bool EstaFirmado, bool TextoLegible,
    IReadOnlyList<TramoDetectado> Tramos, CatalogoDivisionPdf Catalogo);

public class PrepararDivisionPdfQueryHandler(
    IDisponibilidadHerramientasPdf disponibilidad,
    IDivisorPdf divisor,
    IExtractorPalabrasPdf extractorPalabras,
    IMediator mediator)
    : IRequestHandler<PrepararDivisionPdfQuery, Result<PreparacionDivisionPdf>>
{
    public const string ErrorNoDisponible = "DivisionPdf.NoDisponible";

    public async Task<Result<PreparacionDivisionPdf>> Handle(PrepararDivisionPdfQuery request, CancellationToken cancellationToken)
    {
        if (!await disponibilidad.EstaDisponibleAsync(cancellationToken))
            return Result.Fallo<PreparacionDivisionPdf>(Error.Crear(ErrorNoDisponible, "Herramientas PDF no está disponible."));

        var catalogo = await mediator.Send(new ObtenerCatalogoDivisionPdfQuery(request.EmpresaGestionadaId), cancellationToken);
        if (!catalogo.EsExitoso)
            return Result.Fallo<PreparacionDivisionPdf>(catalogo.Error);

        var lectura = await LeerAsync(request.ContenidoPdf, cancellationToken);
        if (!lectura.EsExitoso)
            return Result.Fallo<PreparacionDivisionPdf>(lectura.Error);

        var (numeroPaginas, firmado, palabras) = lectura.Valor;
        var tramos = await Task.Run(() => DetectorTramosPdf.Detectar(palabras ?? [], numeroPaginas, catalogo.Valor), cancellationToken);

        return Result.Exito(new PreparacionDivisionPdf(numeroPaginas, firmado, palabras is not null, tramos, catalogo.Valor));
    }

    private async Task<Result<(int NumeroPaginas, bool Firmado, IReadOnlyList<PaginaPdf>? Palabras)>> LeerAsync(
        byte[] contenido, CancellationToken cancellationToken)
    {
        // PDFsharp abre el PDF en un proceso aparte con su límite de memoria y de tiempo; solo si lo abre se lee el texto.
        var abierto = await divisor.AbrirAsync(contenido, cancellationToken);
        if (!abierto.EsExitoso)
            return Result.Fallo<(int, bool, IReadOnlyList<PaginaPdf>?)>(abierto.Error);

        using var pdf = abierto.Valor;

        // Si PDFsharp lo abre y PdfPig no, se sigue sin sugerencias: dividirlo a mano sigue siendo posible. PdfPig es
        // síncrono y trabaja con la CPU: en un circuito de Blazor bloquearía la pantalla.
        var palabras = await Task.Run(
            () => extractorPalabras.ExtraerPalabras(contenido, LimitesDivisionPdf.TamanoMaximoOriginalBytes), cancellationToken);

        return Result.Exito((pdf.NumeroPaginas, pdf.EstaFirmado, palabras.EsExitoso ? palabras.Valor : (IReadOnlyList<PaginaPdf>?)null));
    }
}
