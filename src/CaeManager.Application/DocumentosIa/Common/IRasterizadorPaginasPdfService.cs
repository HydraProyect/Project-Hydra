using CaeManager.Domain.Common;

namespace CaeManager.Application.DocumentosIa.Common;

/// <summary>
/// Rasteriza una página de un PDF a PNG para pasarla a un proveedor de OCR.
/// Solo necesario en el Caso Mixto del <see cref="DocumentAIRouterService"/>:
/// evita pagar OCR por páginas que ya contienen texto digital embebido.
///
/// <b>Una página por llamada, y ese es el punto.</b> Antes el método
/// rasterizaba una lista de índices y devolvía <c>IReadOnlyList&lt;byte[]&gt;</c>,
/// así que todas las imágenes de un documento existían a la vez en memoria
/// administrada antes de que empezara el primer OCR. Un PNG a 150 ppp de un A4
/// ronda el megabyte, de modo que un PDF escaneado de doscientas páginas
/// reservaba cientos de megas de golpe — en el mismo proceso que sirve Blazor,
/// porque no hay worker separado. No hacía falta mala fe: basta un escaneado
/// grande. Con una página por llamada, el llamador puede liberar cada imagen en
/// cuanto la ha enviado, y el pico de memoria deja de depender del número de
/// páginas.
///
/// El <see cref="CancellationToken"/> tampoco estaba. La conversión es nativa y
/// síncrona, así que no puede interrumpirse a media página; lo que sí puede es
/// no empezar la siguiente, que es lo que convierte un apagado o un timeout en
/// algo que termina en una página en vez de en el documento entero.
/// </summary>
public interface IRasterizadorPaginasPdfService
{
    /// <param name="indicePagina">Índice 0-based de la página a rasterizar.</param>
    Result<byte[]> RasterizarPagina(byte[] contenidoPdf, int indicePagina, CancellationToken cancellationToken = default);

    /// <summary>
    /// La página reducida a <paramref name="anchoPixeles"/> de ancho, para enseñarla
    /// como miniatura. La Subida múltiple retiene una por archivo pendiente de
    /// confirmar: a 150 ppp cada una rondaba el megabyte para pintarse a 48 px
    /// (S-C-3, ASVS 4.0.3 V12.1.1). Por defecto, la página entera: solo la
    /// implementación real sabe reducirla.
    /// </summary>
    Result<byte[]> RasterizarMiniatura(byte[] contenidoPdf, int indicePagina, int anchoPixeles, CancellationToken cancellationToken = default) =>
        RasterizarPagina(contenidoPdf, indicePagina, cancellationToken);
}
