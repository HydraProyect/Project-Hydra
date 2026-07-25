using CaeManager.Domain.Common;

namespace CaeManager.Application.Common;

/// <summary>
/// Clasifica un archivo antes de mandarlo a ningún proveedor de IA —
/// puramente local, sin llamadas externas (ver docs/ARQUITECTURA-IA-DOCUMENTAL.md,
/// Fase 1). Determina si un PDF tiene texto digital seleccionable, está
/// escaneado (solo imágenes), es mixto, o si el archivo es directamente una
/// imagen suelta — el <c>DocumentAIRouterService</c> (fases siguientes) usa
/// esto para decidir si hace falta OCR antes de la extracción estructurada.
/// </summary>
public interface IClasificadorDocumentoService
{
    Task<Result<ClasificacionDocumentoDto>> ClasificarAsync(
        byte[] contenido, string nombreArchivo, CancellationToken cancellationToken = default);
}

public enum TipoContenidoDocumento
{
    Digital,
    Escaneado,
    Mixto,
    Imagen
}

/// <summary>
/// <paramref name="PaginasConTextoDigital"/>: una entrada por página de un
/// PDF (true = esa página tiene texto real embebido); para <see cref="TipoContenidoDocumento.Imagen"/>
/// (archivo que no es PDF) contiene una única entrada <c>false</c>.
/// </summary>
public record ClasificacionDocumentoDto(
    TipoContenidoDocumento Tipo, int TotalPaginas, IReadOnlyList<bool> PaginasConTextoDigital);
