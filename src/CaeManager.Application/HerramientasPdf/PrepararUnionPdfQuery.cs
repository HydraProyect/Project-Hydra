using CaeManager.Application.Common;
using CaeManager.Application.Common.Archivos;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>
/// Lee un PDF que sube una persona para unirlo y dice cuántas páginas tiene y si lleva firma, sin escribir nada. La
/// lectura es solo local, como la de Dividir: este handler no conoce ningún puerto de IA.
/// </summary>
public record PrepararUnionPdfQuery(byte[] ContenidoPdf, string NombreArchivo) : IRequest<Result<InfoPdfParaUnir>>;

/// <summary>Lo que se exige a un PDF subido, antes de abrirlo: el mismo criterio al añadirlo y al unirlo.</summary>
internal static class ValidacionPdfSubidoParaUnir
{
    /// <summary>El código de <see cref="ErroresUnirPdf"/> que lo rechaza, o <c>null</c> si sirve.</summary>
    public static string? Rechazo(byte[] contenido, string? nombreArchivo)
    {
        if (contenido.Length == 0)
            return ErroresUnirPdf.Ilegible;

        if (contenido.Length > LimitesArchivoSubido.TamanoMaximoBytes)
            return ErroresUnirPdf.DemasiadoGrande;

        return !string.Equals(Path.GetExtension(nombreArchivo), ".pdf", StringComparison.OrdinalIgnoreCase)
            || !ValidadorFirmaArchivo.EsDocumentoValido(contenido, nombreArchivo)
            ? ErroresUnirPdf.NoEsPdf
            : null;
    }
}

public class PrepararUnionPdfQueryHandler(IDisponibilidadHerramientasPdf disponibilidad, IUnidorPdf unidor)
    : IRequestHandler<PrepararUnionPdfQuery, Result<InfoPdfParaUnir>>
{
    public async Task<Result<InfoPdfParaUnir>> Handle(PrepararUnionPdfQuery request, CancellationToken cancellationToken)
    {
        if (!await disponibilidad.EstaDisponibleAsync(cancellationToken))
            return Result.Fallo<InfoPdfParaUnir>(Error.Crear(ErroresUnirPdf.NoDisponible, "Herramientas PDF no está disponible."));

        if (ValidacionPdfSubidoParaUnir.Rechazo(request.ContenidoPdf, request.NombreArchivo) is { } rechazo)
            return Result.Fallo<InfoPdfParaUnir>(Error.Crear(rechazo, "El PDF no sirve para unirlo."));

        // PDFsharp es síncrono y trabaja con la CPU: en un circuito de Blazor bloquearía la pantalla.
        return await Task.Run(() => unidor.Inspeccionar(request.ContenidoPdf), cancellationToken);
    }
}
