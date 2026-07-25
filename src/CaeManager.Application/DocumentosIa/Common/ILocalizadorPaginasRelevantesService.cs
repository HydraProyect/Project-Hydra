namespace CaeManager.Application.DocumentosIa.Common;

/// <summary>
/// Para documentos digitales grandes (pólizas de cientos de páginas, ver
/// docs/ARQUITECTURA-IA-DOCUMENTAL.md § 4.3): en vez de mandar el texto
/// completo a IA, localiza qué páginas contienen información relevante
/// (número de póliza, tomador, fechas, firma...) para mandar solo esas —
/// reduce drásticamente el coste en tokens sin perder los datos que
/// importan. Puramente local (búsqueda de palabras clave), sin IA — mismo
/// criterio que <c>IClasificadorDocumentoService</c>.
/// </summary>
public interface ILocalizadorPaginasRelevantesService
{
    /// <summary>Índices (0-based) de las páginas relevantes, en orden ascendente — nunca vacío si <paramref name="textoPorPagina"/> tiene al menos una página.</summary>
    IReadOnlyList<int> Localizar(IReadOnlyList<string> textoPorPagina);
}
