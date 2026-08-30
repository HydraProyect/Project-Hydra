using System.Text.RegularExpressions;
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
///
/// Dos huecos deliberadamente distintos de "falta una palabra clave más":
/// una fecha puede aparecer sin ir acompañada de ninguna de las palabras
/// de <see cref="PalabrasClave"/> (una tabla de revisiones, una cláusula
/// con plazo pero sin la palabra "vencimiento" al lado), y una cláusula de
/// exclusión describe justamente lo que el documento NO cubre — la lista
/// original solo buscaba vocabulario de lo que SÍ cubre, así que la
/// ausencia no era casual, era estructural. <see cref="PatronFecha"/> cubre
/// el primero con un patrón de formato en vez de vocabulario; el segundo
/// se cubre añadiendo el vocabulario de exclusión a <see cref="PalabrasClave"/>.
/// Ninguno de los dos hace que las decisiones automáticas confíen en esta
/// selección — sigue siendo una selección de qué texto se envía a IA, no
/// una decisión sobre el documento.
/// </summary>
public class LocalizadorPaginasRelevantesService : ILocalizadorPaginasRelevantesService
{
    private static readonly string[] PalabrasClave =
    [
        "póliza", "poliza", "tomador", "asegurado", "aseguradora", "prima", "cobertura", "coberturas",
        "capital asegurado", "vigencia", "vencimiento", "fecha de efecto", "fecha de emisión", "fecha de expedición",
        "firma", "firmado", "sello", "importe", "total a pagar", "recibo", "certificado", "certifica",
        "cif", "nif", "dni", "razón social", "domicilio social", "número de referencia", "n.º de póliza",

        // Exclusiones y limitaciones: describen lo que el documento NO
        // cubre, y por eso rara vez comparten página con "cobertura",
        // "asegurado" o cualquier otra palabra de la lista de arriba.
        "exclusión", "exclusion", "exclusiones", "excluye", "excluido", "excluida", "excluidos", "excluidas",
        "no cubre", "no cubierto", "no incluye", "no aplica", "salvo", "excepto", "limitación", "limitaciones",
    ];

    /// <summary>
    /// Formatos habituales en documentos CAE/seguros en español: numérico
    /// (15/03/2027, 15-03-2027, 15.03.2027, con año de 2 o 4 cifras), ISO
    /// (2027-03-15) y en letra (15 de marzo de 2027). No intenta validar que
    /// la fecha exista de verdad (31 de febrero pasaría) — es un filtro de
    /// selección de páginas, no una validación de datos, y rechazar un falso
    /// positivo aquí es gratis (la página de más no cuesta nada grave)
    /// mientras que perder una página con una fecha real sí.
    /// </summary>
    private static readonly Regex PatronFecha = new(
        """\b(\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}|\d{4}-\d{2}-\d{2}|\d{1,2}\s+de\s+(enero|febrero|marzo|abril|mayo|junio|julio|agosto|septiembre|octubre|noviembre|diciembre)\s+de\s+\d{4})\b""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<int> Localizar(IReadOnlyList<string> textoPorPagina)
    {
        if (textoPorPagina.Count == 0)
            return [];

        var indices = new SortedSet<int>();

        for (var i = 0; i < textoPorPagina.Count; i++)
        {
            if (PalabrasClave.Any(palabra => textoPorPagina[i].Contains(palabra, StringComparison.OrdinalIgnoreCase))
                || PatronFecha.IsMatch(textoPorPagina[i]))
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
