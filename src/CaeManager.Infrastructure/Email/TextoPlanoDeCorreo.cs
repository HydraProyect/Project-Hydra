using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CaeManager.Infrastructure.Email;

/// <summary>
/// Deriva la parte <c>text/plain</c> de un correo a partir de su HTML ya
/// envuelto en la plantilla de marca.
///
/// <para>
/// <b>Por qué existe.</b> Muchas organizaciones bloquean las imágenes de
/// remitentes externos, y algunas muestran solo texto plano (lectores de
/// pantalla, clientes en modo texto, filtros que descartan el HTML). Un
/// correo que solo trae HTML queda entonces vacío o ilegible; además, un
/// mensaje HTML sin alternativa de texto puntúa peor en los filtros de spam.
/// </para>
///
/// <para>
/// <b>Se deriva, no se escribe aparte.</b> Cada llamador compone solo su HTML;
/// mantener dos versiones a mano en cada sitio acabaría con un texto plano
/// que dice otra cosa que el HTML. La conversión conserva lo que importa para
/// actuar: cada enlace se escribe como «texto (url)» para que la llamada a la
/// acción siga siendo utilizable sin HTML, cada imagen se sustituye por su
/// <c>alt</c>, y los saltos de bloque (párrafo, fila, título, lista) se
/// convierten en saltos de línea.
/// </para>
/// </summary>
internal static partial class TextoPlanoDeCorreo
{
    private const int TopeRegexMs = 500;

    public static string DesdeHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        try
        {
            return Convertir(html);
        }
        catch (RegexMatchTimeoutException)
        {
            // Un HTML patológico (miles de etiquetas sin cerrar) no debe impedir el envío ni agotar la CPU:
            // se cae a quitar etiquetas sin expresiones regulares, perdiendo los enlaces pero no el mensaje.
            return Normalizar(WebUtility.HtmlDecode(SinEtiquetas(html)));
        }
    }

    // Decodificar entidades ocurre una sola vez, al final: lo que se inserta antes (texto y dirección de los
    // enlaces, alt de las imágenes) se deja tal cual viene del HTML, para que «&amp;lt;» no acabe como «<».
    private static string Convertir(string html)
    {
        var texto = BloquesSinTexto().Replace(html, string.Empty);
        texto = Comentarios().Replace(texto, string.Empty);
        texto = Enlaces().Replace(texto, EscribirEnlace);
        texto = Imagenes().Replace(texto, m => Atributo(m.Value, "alt") is { Length: > 0 } alt ? alt : string.Empty);
        texto = ElementoDeLista().Replace(texto, "\n- ");
        texto = SaltoDeLinea().Replace(texto, "\n");
        texto = Fila().Replace(texto, "\n");
        // Un bloque (párrafo, título, lista, tabla) se separa con una línea en blanco, venga o no un
        // salto de línea en el HTML: así la forma del texto no depende de cómo se indentó la plantilla.
        texto = FinDeBloque().Replace(texto, "\n\n");
        texto = Celda().Replace(texto, " ");
        texto = Etiquetas().Replace(texto, string.Empty);
        texto = WebUtility.HtmlDecode(texto);

        return Normalizar(texto);
    }

    private static string EscribirEnlace(Match m)
    {
        var href = m.Groups["href"].Value.Trim();
        var interior = Etiquetas().Replace(m.Groups["texto"].Value, string.Empty);
        var texto = Espacios().Replace(interior, " ").Trim();

        if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            var direccion = href["mailto:".Length..];
            return string.IsNullOrEmpty(texto) || string.Equals(texto, direccion, StringComparison.OrdinalIgnoreCase)
                ? direccion
                : $"{texto} ({direccion})";
        }

        if (string.IsNullOrEmpty(href) || href.StartsWith('#'))
            return texto;

        if (string.IsNullOrEmpty(texto) || string.Equals(texto.TrimEnd('/'), href.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return href;

        // Un texto que ya es el dominio de la url («talveg.es» → https://talveg.es) no necesita repetirla.
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri)
            && string.Equals(texto.TrimEnd('/'), uri.Host, StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath is "/" or "")
            return href;

        return $"{texto} ({href})";
    }

    internal static string SinEtiquetas(string html)
    {
        // Sin regex: lo anterior al cierre de <head> (estilos y título de la plantilla) no es texto del mensaje.
        var finCabecera = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (finCabecera >= 0)
            html = html[(finCabecera + "</head>".Length)..];

        var sb = new StringBuilder(html.Length);
        var dentro = false;
        foreach (var c in html)
        {
            if (c == '<')
                dentro = true;
            else if (c == '>' && dentro)
            {
                dentro = false;
                sb.Append(' ');
            }
            else if (!dentro)
                sb.Append(c);
        }

        return sb.ToString();
    }

    private static string? Atributo(string etiqueta, string nombre)
    {
        var m = Regex.Match(etiqueta, $"""\b{nombre}\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)'|(?<v>[^\s"'>]+))""", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(TopeRegexMs));
        return m.Success ? m.Groups["v"].Value.Trim() : null;
    }

    private static string Normalizar(string texto)
    {
        var lineas = texto.Replace("\r\n", "\n").Split('\n')
            .Select(l => Espacios().Replace(l, " ").Trim());

        var resultado = new StringBuilder();
        var vacias = 0;
        foreach (var linea in lineas)
        {
            if (linea.Length == 0)
            {
                vacias++;
                continue;
            }

            if (resultado.Length > 0)
                resultado.Append(vacias > 0 ? "\n\n" : "\n");

            resultado.Append(linea);
            vacias = 0;
        }

        return resultado.ToString();
    }

    [GeneratedRegex(@"<(head|style|script|title)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: TopeRegexMs)]
    private static partial Regex BloquesSinTexto();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline, matchTimeoutMilliseconds: TopeRegexMs)]
    private static partial Regex Comentarios();

    [GeneratedRegex("""<a\b[^>]*?\bhref\s*=\s*(?:"(?<href>[^"]*)"|'(?<href>[^']*)'|(?<href>[^\s"'>]+))[^>]*>(?<texto>.*?)</a\s*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: TopeRegexMs)]
    private static partial Regex Enlaces();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: TopeRegexMs)]
    private static partial Regex Imagenes();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: TopeRegexMs)]
    private static partial Regex ElementoDeLista();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: TopeRegexMs)]
    private static partial Regex SaltoDeLinea();

    [GeneratedRegex(@"</?(p|div|h[1-6]|table|ul|ol|blockquote)\b[^>]*>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: TopeRegexMs)]
    private static partial Regex FinDeBloque();

    [GeneratedRegex(@"</?tr\b[^>]*>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: TopeRegexMs)]
    private static partial Regex Fila();

    [GeneratedRegex(@"</?t[dh]\b[^>]*>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: TopeRegexMs)]
    private static partial Regex Celda();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.None, matchTimeoutMilliseconds: TopeRegexMs)]
    private static partial Regex Etiquetas();

    [GeneratedRegex(@"[ \t ]+", RegexOptions.None, matchTimeoutMilliseconds: TopeRegexMs)]
    private static partial Regex Espacios();
}
