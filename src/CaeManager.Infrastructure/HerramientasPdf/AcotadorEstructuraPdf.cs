using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using CaeManager.Application.HerramientasPdf;

namespace CaeManager.Infrastructure.HerramientasPdf;

/// <summary>
/// Mide, sin PDFsharp, lo que costará abrir un PDF: PDFsharp carga todos sus objetos al abrirlo (también en modo
/// importación, ver <c>LectorRecuentoPaginasPdfSinAbrir</c>) y no deja acotar ni interrumpir ese trabajo. Un PDF pequeño
/// puede llevar millones de objetos dentro de flujos comprimidos (<c>/Type /ObjStm</c>) o una tabla de referencias
/// comprimida (<c>/Type /XRef</c>) que se descomprime a gigas: el lector previo de páginas se abstiene con esas formas, y el
/// tope de páginas llegaría tarde. OWASP API4:2023, CWE-409.
///
/// <para>
/// Qué mide: los objetos en texto plano (cada cabecera <c>N G obj</c>), los que declara cada flujo de objetos (<c>/N</c>) y
/// los bytes que se descomprimen de esos flujos y de las tablas comprimidas, descomprimiendo con tope: se para en cuanto se
/// pasa de <see cref="LimitesUnionPdf.MaximoBytesEstructuraPorPdf"/>. Los nombres se leen decodificados (<c>/Obj#53tm</c> es
/// <c>/ObjStm</c>), como los lee PDFsharp. No descomprime contenido de páginas ni imágenes: PDFsharp no los analiza al
/// abrir, y un PDF escaneado legítimo pesa ahí.
/// </para>
///
/// <para>
/// Falla en cerrado: un flujo de objetos o una tabla comprimida que no se puede medir (filtro distinto de Flate, datos que no
/// descomprimen, diccionario sin cabecera de objeto cerca) es un PDF «demasiado complejo», no uno que se abre a ciegas.
/// Los generadores habituales (Word, LibreOffice, navegadores, escáneres) usan Flate, así que no se rechaza ninguno legítimo
/// por la forma; solo por el tamaño de su estructura.
/// </para>
///
/// <para>
/// Es un primer filtro barato, no la garantía. Mira una ventana acotada antes de cada <c>stream</c> y una forma conocida
/// del diccionario, y un PDF fabricado puede esconderle un flujo de objetos (por ejemplo, con relleno que aleje la cabecera
/// del objeto más allá de la ventana) o cargar a PDFsharp por donde no mira (matrices enormes). La garantía es que PDFsharp
/// solo abre PDF ajenos en el proceso aparte (<see cref="ProcesoHerramientasPdf"/>), con un límite duro de memoria y de
/// tiempo: lo que se escape de aquí agota ese proceso y sale como «demasiado complejo», con la aplicación viva.
/// </para>
/// </summary>
internal static class AcotadorEstructuraPdf
{
    /// <summary>Lo que se mira hacia atrás desde <c>stream</c> para encontrar el diccionario y la cabecera del objeto.</summary>
    private const int VentanaDiccionario = 16 * 1024;

    /// <summary>Lo que se mira de un flujo sin cabecera de objeto cerca, para saber si es de estructura (y rechazarlo).</summary>
    private const int VentanaSinCabecera = 1024;

    private static readonly TimeSpan TiempoMaximoRegex = TimeSpan.FromMilliseconds(200);
    private static readonly Regex NombreCodificado = new("#([0-9A-Fa-f]{2})", RegexOptions.CultureInvariant, TiempoMaximoRegex);
    private static readonly Regex CabeceraObjeto = new(@"\d+[ \t\r\n\f\0]+\d+[ \t\r\n\f\0]+obj", RegexOptions.CultureInvariant, TiempoMaximoRegex);
    private static readonly Regex Entero = new(@"^\s*(\d{1,12})", RegexOptions.CultureInvariant, TiempoMaximoRegex);

    /// <summary>Bytes de estructura descomprimidos, o <c>null</c> si el PDF pasa de los topes o no se puede medir.</summary>
    public static long? Medir(byte[] pdf, int maximoObjetos = LimitesUnionPdf.MaximoObjetosPorPdf,
        long maximoBytes = LimitesUnionPdf.MaximoBytesEstructuraPorPdf)
    {
        long objetos = ContarCabecerasDeObjeto(pdf);
        if (objetos > maximoObjetos)
            return null;

        long descomprimidos = 0;
        var flujos = 0;
        var finFlujoAnterior = 0;
        var span = pdf.AsSpan();
        for (var desde = 0; desde < pdf.Length;)
        {
            var relativo = span[desde..].IndexOf("stream"u8);
            if (relativo < 0)
                break;
            var indice = desde + relativo;
            desde = indice + "stream".Length;

            // «endstream» no abre un flujo; «stream» tiene que ir seguido de fin de línea (ISO 32000-1 § 7.3.8.1).
            if (indice >= 3 && span.Slice(indice - 3, 3).SequenceEqual("end"u8))
                continue;
            var datos = InicioDeDatos(span, desde);
            if (datos < 0)
                continue;

            // Cada flujo es un objeto: más flujos que objetos es un archivo fabricado, y acotarlos acota este bucle.
            if (++flujos > maximoObjetos)
                return null;

            // El diccionario va desde la cabecera de su objeto («N G obj», la última antes de «stream» que no está en un
            // comentario) hasta aquí, y tiene que ser exactamente un diccionario bien formado. Una cabecera falsa metida en
            // una cadena o un comentario del propio diccionario deja un tramo mal formado, y eso falla en cerrado: sin poder
            // delimitar el diccionario no se sabe si es de estructura. Sin cabecera cerca, solo se mira un trozo.
            // Desde el «stream» anterior: el diccionario de este flujo siempre va detrás de los datos del anterior, y así las
            // ventanas no se solapan y todo el recorrido lee el archivo una vez, haya los flujos que haya. La ventana es
            // acotada a propósito: un diccionario con relleno que la desborde se escapa de esta medición, y lo para el límite
            // del proceso aparte, no este filtro.
            var inicioVentana = Math.Max(finFlujoAnterior, indice - VentanaDiccionario);
            finFlujoAnterior = indice + "stream".Length;
            var ventana = Encoding.Latin1.GetString(pdf, inicioVentana, indice - inicioVentana);
            var finCabecera = FinDeCabecera(ventana);
            var conCabecera = finCabecera >= 0;
            var tramo = conCabecera ? ventana[finCabecera..] : ventana[Math.Max(0, ventana.Length - VentanaSinCabecera)..];
            if (conCabecera && !EsUnSoloDiccionario(tramo))
                return null;
            var diccionario = Decodificar(tramo);

            var esFlujoDeObjetos = TieneClave(diccionario, "/Type/ObjStm") || (TieneClave(diccionario, "/N") && TieneClave(diccionario, "/First"));
            var esTablaComprimida = TieneClave(diccionario, "/Type/XRef") || TieneClave(diccionario, "/W");
            if (!esFlujoDeObjetos && !esTablaComprimida)
                continue;

            // Un diccionario de estructura sin cabecera de objeto a la vista no se puede delimitar: no se abre a ciegas.
            if (!conCabecera)
                return null;

            if (esFlujoDeObjetos)
            {
                if (ValorEntero(diccionario, "/N") is not { } declarados || declarados > maximoObjetos - objetos)
                    return null;
                objetos += declarados;
            }

            if (esTablaComprimida && ValorEntero(diccionario, "/Size") is { } tamano && tamano > maximoObjetos)
                return null;

            if (!TieneClave(diccionario, "/Filter"))
                continue; // Sin comprimir: su coste ya está en los bytes del archivo.
            if (!TieneClave(diccionario, "/FlateDecode") || FiltrosDistintosDeFlate(diccionario))
                return null;

            var medidos = Descomprimir(pdf, datos, maximoBytes - descomprimidos);
            if (medidos is null)
                return null;
            descomprimidos += medidos.Value;
        }

        return descomprimidos;
    }

    private static int ContarCabecerasDeObjeto(byte[] pdf)
    {
        // Cota superior barata: cada «obj» que no es «endobj». Cuenta de más, nunca de menos.
        var span = pdf.AsSpan();
        var cuenta = 0;
        for (var desde = 0; desde < pdf.Length;)
        {
            var relativo = span[desde..].IndexOf("obj"u8);
            if (relativo < 0)
                break;
            var indice = desde + relativo;
            if (indice < 3 || !span.Slice(indice - 3, 3).SequenceEqual("end"u8))
                cuenta++;
            desde = indice + 3;
        }

        return cuenta;
    }

    /// <summary>Posición tras la última cabecera «N G obj» de la ventana que no está en una línea comentada; -1 si no hay.</summary>
    private static int FinDeCabecera(string ventana)
    {
        var fin = -1;
        for (var candidata = CabeceraObjeto.Match(ventana); candidata.Success; candidata = candidata.NextMatch())
        {
            var siguiente = candidata.Index + candidata.Length;
            if (siguiente < ventana.Length && char.IsAsciiLetterOrDigit(ventana[siguiente]))
                continue; // «objeto», no la palabra clave
            var inicioLinea = candidata.Index == 0 ? 0 : ventana.LastIndexOfAny(['\n', '\r'], candidata.Index - 1) + 1;
            if (ventana.IndexOf('%', inicioLinea, candidata.Index - inicioLinea) < 0)
                fin = siguiente;
        }

        return fin;
    }

    /// <summary>
    /// Exactamente un diccionario PDF (con cadenas, cadenas hexadecimales, nombres, números, referencias, matrices,
    /// diccionarios anidados y comentarios), seguido solo de blancos o comentarios. Cualquier otra cosa es un tramo que no
    /// se ha podido delimitar.
    /// </summary>
    private static bool EsUnSoloDiccionario(string tramo)
    {
        var i = SaltarBlancos(tramo, 0);
        if (i + 1 >= tramo.Length || tramo[i] != '<' || tramo[i + 1] != '<')
            return false;

        var profundidad = 0;
        while (i < tramo.Length)
        {
            var c = tramo[i];
            if (c == '<' && i + 1 < tramo.Length && tramo[i + 1] == '<') { profundidad++; i += 2; }
            else if (c == '>' && i + 1 < tramo.Length && tramo[i + 1] == '>')
            {
                profundidad--; i += 2;
                if (profundidad == 0)
                    return SaltarBlancos(tramo, i) == tramo.Length;
            }
            else if (c == '<')
            {
                var cierre = tramo.IndexOf('>', i + 1);
                if (cierre < 0 || tramo[(i + 1)..cierre].Any(h => !Uri.IsHexDigit(h) && !char.IsWhiteSpace(h))) return false;
                i = cierre + 1;
            }
            else if (c == '(')
            {
                var anidadas = 1;
                for (i++; i < tramo.Length && anidadas > 0; i++)
                {
                    if (tramo[i] == '\\') i++;
                    else if (tramo[i] == '(') anidadas++;
                    else if (tramo[i] == ')') anidadas--;
                }
                if (anidadas > 0) return false;
            }
            else if (c == '%') { while (i < tramo.Length && tramo[i] is not ('\n' or '\r')) i++; }
            else if (c is ')' or '>' or '{' or '}' or '\\') return false;
            else i++;
        }

        return false;
    }

    private static int SaltarBlancos(string tramo, int i)
    {
        while (i < tramo.Length)
        {
            if (char.IsWhiteSpace(tramo[i]) || tramo[i] == '\0') i++;
            else if (tramo[i] == '%') { while (i < tramo.Length && tramo[i] is not ('\n' or '\r')) i++; }
            else break;
        }

        return i;
    }

    private static int InicioDeDatos(ReadOnlySpan<byte> pdf, int trasPalabra)
    {
        if (trasPalabra < pdf.Length && pdf[trasPalabra] == (byte)'\n')
            return trasPalabra + 1;
        if (trasPalabra + 1 < pdf.Length && pdf[trasPalabra] == (byte)'\r' && pdf[trasPalabra + 1] == (byte)'\n')
            return trasPalabra + 2;
        return -1;
    }

    /// <summary>Descomprime con tope; <c>null</c> si pasa de <paramref name="maximo"/> o los datos no son Flate válido.</summary>
    private static long? Descomprimir(byte[] pdf, int inicio, long maximo)
    {
        try
        {
            using var origen = new ZLibStream(new MemoryStream(pdf, inicio, pdf.Length - inicio, writable: false), CompressionMode.Decompress);
            var bloque = new byte[81_920];
            long total = 0;
            int leidos;
            while ((leidos = origen.Read(bloque, 0, bloque.Length)) > 0)
            {
                total += leidos;
                if (total > maximo)
                    return null;
            }

            return total;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static string Decodificar(string diccionario)
    {
        var sinEscapes = NombreCodificado.Replace(diccionario, m => ((char)Convert.ToByte(m.Groups[1].Value, 16)).ToString());
        // Los blancos entre una clave y su nombre no cambian nada: /Type /ObjStm y /Type/ObjStm son lo mismo.
        return Regex.Replace(sinEscapes, @"\s*/\s*", "/", RegexOptions.None, TiempoMaximoRegex);
    }

    /// <summary>La clave o la secuencia de nombres, seguida de algo que no continúa el nombre.</summary>
    private static bool TieneClave(string diccionario, string clave)
    {
        for (var desde = 0; ;)
        {
            var indice = diccionario.IndexOf(clave, desde, StringComparison.Ordinal);
            if (indice < 0)
                return false;
            var siguiente = indice + clave.Length;
            if (siguiente >= diccionario.Length || !char.IsLetterOrDigit(diccionario[siguiente]))
                return true;
            desde = indice + 1;
        }
    }

    private static long? ValorEntero(string diccionario, string clave)
    {
        for (var desde = 0; ;)
        {
            var indice = diccionario.IndexOf(clave, desde, StringComparison.Ordinal);
            if (indice < 0)
                return null;
            var siguiente = indice + clave.Length;
            if (siguiente < diccionario.Length && !char.IsLetterOrDigit(diccionario[siguiente]))
            {
                var valor = Entero.Match(diccionario[siguiente..]);
                if (valor.Success)
                    return long.Parse(valor.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            desde = indice + 1;
        }
    }

    private static bool FiltrosDistintosDeFlate(string diccionario) =>
        new[] { "/LZWDecode", "/ASCIIHexDecode", "/ASCII85Decode", "/RunLengthDecode", "/CCITTFaxDecode", "/DCTDecode",
                "/JBIG2Decode", "/JPXDecode", "/Crypt", "/LZW", "/AHx", "/A85", "/RL" }
            .Any(f => TieneClave(diccionario, f));
}
