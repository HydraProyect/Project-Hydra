using CaeManager.Application.DocumentosIa.Common;

namespace CaeManager.Application.DocumentosIa;

/// <summary>
/// Implementación v1, deliberadamente simple: búsqueda de palabras clave
/// (sin distinguir mayúsculas), sin IA ni embeddings — suficiente para el
/// caso real que motivó esto (una póliza de 280 páginas donde los datos
/// que importan aparecen en menos de 10, ver Issue #19). Lista de palabras
/// clave orientada a documentos CAE/seguros en español; ampliable sin
/// tocar el resto del pipeline si aparecen tipos de documento con otro
/// vocabulario relevante.
/// </summary>
public class LocalizadorPaginasRelevantesService : ILocalizadorPaginasRelevantesService
{
    private static readonly string[] PalabrasClave =
    [
        "póliza", "poliza", "tomador", "asegurado", "aseguradora", "prima", "cobertura", "coberturas",
        "capital asegurado", "vigencia", "vencimiento", "fecha de efecto", "fecha de emisión", "fecha de expedición",
        "firma", "firmado", "sello", "importe", "total a pagar", "recibo", "certificado", "certifica",
        "cif", "nif", "dni", "razón social", "domicilio social", "número de referencia", "n.º de póliza",
    ];

    public IReadOnlyList<int> Localizar(IReadOnlyList<string> textoPorPagina)
    {
        if (textoPorPagina.Count == 0)
            return [];

        var indices = new SortedSet<int>();

        for (var i = 0; i < textoPorPagina.Count; i++)
        {
            if (PalabrasClave.Any(palabra => textoPorPagina[i].Contains(palabra, StringComparison.OrdinalIgnoreCase)))
                indices.Add(i);
        }

        // La portada y la última página (firma/cierre) casi siempre importan,
        // aunque su texto no contenga ninguna palabra clave (p. ej. un logo
        // con poco texto real).
        indices.Add(0);
        indices.Add(textoPorPagina.Count - 1);

        return indices.ToList();
    }
}
