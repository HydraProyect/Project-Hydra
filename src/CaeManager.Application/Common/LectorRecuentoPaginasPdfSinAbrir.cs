using System.Text;
using System.Text.RegularExpressions;

namespace CaeManager.Application.Common;

/// <summary>
/// Lee cuántas páginas declara un PDF SIN invocar PdfSharp — sin pagar el
/// coste de que <c>PdfReader.Open</c> parsee el árbol de páginas completo.
/// Medido sobre un PDF real de 20 000 páginas (probe REC-176/REC-186) para
/// los cuatro <c>PdfDocumentOpenMode</c> públicos de PdfSharp 6.2.4:
/// <c>Modify</c> 711-754 ms (~101 MB), <c>Import</c> 335-380 ms (~82 MB),
/// <c>ReadOnly</c>/<c>InformationOnly</c> ~220-258 ms (~81 MB). No miden
/// igual entre sí — <c>Modify</c> es visiblemente el más caro — pero los
/// CUATRO construyen el árbol de páginas completo antes de que el llamador
/// pueda mirar nada: no hay un modo más barato en la API pública, así que
/// la única forma de no pagar ESE coste (sea cual sea el modo) es no
/// invocar <c>PdfReader.Open</c> en absoluto para decidir el rechazo.
///
/// GEMELO DELIBERADO, NO COMPARTIDO, de
/// <c>CaeManager.Web.Documentos.ConversorArchivosPdf.IntentarLeerRecuentoDePaginasSinAbrir</c>
/// (REC-176): misma lógica exacta, duplicada aquí a propósito porque
/// ConversorArchivosPdf vive en CaeManager.Web y este tope aplica también a
/// sitios de CaeManager.Infrastructure, que no puede depender de Web
/// (Web → Infrastructure → Application → Domain, nunca al revés). El
/// encargo de REC-186 (HO-186-01 § 9) prohíbe explícitamente tocar
/// ConversorArchivosPdf, así que la alternativa es esta clase hermana en
/// Application — capa visible tanto desde Infrastructure como desde Web —
/// en vez de mover o compartir código con el sitio ya cerrado.
///
/// Cota exacta para la forma "clásica" de PDF (trailer, Root, Pages y Count
/// en texto plano, sin xref comprimida ni object streams) y ABSTENCIÓN
/// segura (null) para todo lo demás — nunca una aproximación. Ver el
/// doc-comment de <see cref="IntentarLeerRecuentoDePaginasSinAbrir"/> para
/// el detalle exacto de qué formas cubre y por qué el contrato de PDF (ISO
/// 32000-1 § 7.7.3.2) hace que leer solo <c>/Count</c> del nodo raíz baste,
/// sin bajar a <c>/Kids</c> ni sumar nada.
/// </summary>
public static class LectorRecuentoPaginasPdfSinAbrir
{
    /// <summary>Tiempo máximo por búsqueda textual — defensa en profundidad sobre bytes no confiables; ningún patrón de aquí necesita de verdad tanto.</summary>
    private static readonly TimeSpan TimeoutBusquedaTextual = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Intenta leer cuántas páginas declara este PDF sin invocar
    /// <c>PdfReader</c>. Solo cubre la forma "clásica": un <c>trailer</c>
    /// literal en texto plano cuyo <c>/Root</c> lleva a un <c>/Catalog</c>
    /// también en texto plano, cuyo <c>/Pages</c> lleva al nodo raíz del
    /// árbol de páginas, también en texto plano, con su <c>/Count</c>
    /// declarado ahí mismo. Por el contrato de PDF, ese <c>/Count</c> ya es
    /// el total de páginas hoja de todo el árbol.
    ///
    /// Si el documento tiene actualizaciones incrementales (varios
    /// <c>trailer</c>), usa el ÚLTIMO — es el vigente — y si el objeto
    /// referenciado se reescribió más adelante en el fichero, también usa
    /// su última aparición textual.
    ///
    /// Cuando el documento no tiene esa forma —cifrado, con xref stream
    /// (PDF 1.5+, sin la palabra <c>trailer</c>) o con esos objetos dentro
    /// de un object stream comprimido— este método se ABSTIENE devolviendo
    /// null en vez de adivinar un número: la comprobación de siempre tras
    /// <c>PdfReader.Open</c>, en cada sitio que use esto, sigue siendo la
    /// red de seguridad para lo que este pre-escaneo no cubre — la decisión
    /// de aceptar o rechazar un documento nunca cambia por su causa, solo
    /// cambia CUÁNDO se paga el coste de descubrirlo.
    /// </summary>
    public static long? IntentarLeerRecuentoDePaginasSinAbrir(byte[] contenidoPdf)
    {
        try
        {
            // Latin1: cada byte se conserva 1:1 como char, así el
            // desplazamiento en el string coincide con el desplazamiento en
            // bytes del PDF — no hace falta decodificar el documento de
            // verdad para leer los pocos tokens ASCII de la sintaxis de PDF
            // que interesan aquí.
            var texto = Encoding.Latin1.GetString(contenidoPdf);

            var indiceTrailer = texto.LastIndexOf("trailer", StringComparison.Ordinal);
            if (indiceTrailer < 0) return null;

            if (BuscarReferencia(texto, "/Root", indiceTrailer) is not (long numeroRoot, long generacionRoot))
                return null;

            var cuerpoRoot = BuscarUltimoCuerpoDeObjeto(texto, numeroRoot, generacionRoot);
            if (cuerpoRoot is null) return null;

            if (BuscarReferencia(cuerpoRoot, "/Pages", 0) is not (long numeroPages, long generacionPages))
                return null;

            var cuerpoPages = BuscarUltimoCuerpoDeObjeto(texto, numeroPages, generacionPages);
            if (cuerpoPages is null) return null;
            if (!cuerpoPages.Contains("/Type/Pages", StringComparison.Ordinal) &&
                !cuerpoPages.Contains("/Type /Pages", StringComparison.Ordinal))
                return null;

            var coincidencia = Regex.Match(cuerpoPages, @"/Count\s+(\d+)", RegexOptions.None, TimeoutBusquedaTextual);
            if (!coincidencia.Success) return null;

            // Un /Count que no cabe en long es, para cualquier tope razonable,
            // "se pasa igual" — no hace falta un OverflowException para saberlo.
            return long.TryParse(coincidencia.Groups[1].Value, out var numeroDePaginas)
                ? numeroDePaginas
                : long.MaxValue;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            // Contrato de esta función: SIEMPRE se abstiene (null) en vez de
            // lanzar, sobre bytes que vienen de una fuente no confiable —
            // subida, adjunto de webhook o blob de plantilla. La frontera
            // explícita entre "no pude determinarlo con seguridad" (null,
            // cae a la comprobación de siempre en el sitio que llama) y
            // cualquier forma en que un texto adversario pueda violar un
            // supuesto de este pre-escaneo (regex con tiempo agotado,
            // índices fuera de rango...) debe terminar igual aquí. Mismo
            // contrato y mismos casos límite que
            // ConversorArchivosPdf.IntentarLeerRecuentoDePaginasSinAbrir
            // (REC-176), verificados ahí — ver
            // LectorRecuentoPaginasPdfSinAbrirTests para la cobertura
            // equivalente sobre este gemelo.
        }

        return null;
    }

    /// <summary>
    /// Busca "/etiqueta N G R" a partir de <paramref name="desdeIndice"/> y
    /// devuelve (N, G), o null si no aparece con esa forma. El lookahead
    /// tras la etiqueta exige un delimitador de nombre de PDF (blanco,
    /// paréntesis, corchete, otra barra...) o el final del texto — si no,
    /// la etiqueta buscada sería solo el PREFIJO de un nombre más largo
    /// (p.ej. "/Pages" dentro de "/PagesBackup"), y no la clave real.
    /// </summary>
    private static (long numero, long generacion)? BuscarReferencia(string texto, string etiqueta, int desdeIndice)
    {
        var patron = Regex.Escape(etiqueta) + @"(?=[\s()<>\[\]{}/%]|$)\s+(\d+)\s+(\d+)\s+R";
        var coincidencia = new Regex(patron, RegexOptions.None, TimeoutBusquedaTextual).Match(texto, desdeIndice);
        if (!coincidencia.Success) return null;

        if (!long.TryParse(coincidencia.Groups[1].Value, out var numero)) return null;
        if (!long.TryParse(coincidencia.Groups[2].Value, out var generacion)) return null;
        return (numero, generacion);
    }

    /// <summary>
    /// Busca la ÚLTIMA aparición textual de "N G obj" (la vigente si el
    /// objeto se reescribió por una actualización incremental) y devuelve
    /// el texto entre esa cabecera y el "endobj" que le sigue, o null si no
    /// se encuentra ninguna de las dos cosas.
    /// </summary>
    private static string? BuscarUltimoCuerpoDeObjeto(string texto, long numero, long generacion)
    {
        var patronCabecera = new Regex($@"(?<![0-9]){numero}\s+{generacion}\s+obj\b", RegexOptions.None, TimeoutBusquedaTextual);

        Match? ultimaCabecera = null;
        foreach (Match m in patronCabecera.Matches(texto))
            ultimaCabecera = m;
        if (ultimaCabecera is null) return null;

        var inicioCuerpo = ultimaCabecera.Index + ultimaCabecera.Length;
        var finCuerpo = texto.IndexOf("endobj", inicioCuerpo, StringComparison.Ordinal);
        return finCuerpo < 0 ? null : texto[inicioCuerpo..finCuerpo];
    }
}
