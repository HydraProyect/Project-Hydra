using System.Net;
using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Email;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace CaeManager.Web.Tests.Email;

/// <summary>
/// Muchas organizaciones bloquean las imágenes de remitentes externos, y algunos clientes descartan
/// el bloque <c>&lt;style&gt;</c> o leen solo texto plano. Estos tests fijan que todo correo de TALVEG
/// se entiende igual en esas condiciones: la marca es texto, no hay imágenes de fondo, cada
/// <c>&lt;img&gt;</c> (si alguna vez vuelve a haberla) lleva <c>alt</c>, cada llamada a la acción es
/// un enlace con texto y su dirección visible, y el mensaje lleva una parte <c>text/plain</c>
/// equivalente.
/// </summary>
public class CorreoLegibleSinImagenesTests
{
    private static readonly SmtpEmailOptions OpcionesCompletas = new()
    {
        Host = "mail.talveg.es",
        Usuario = "usuario",
        Contrasena = "contrasena",
        BuzonRemitente = "info@talveg.es",
        UrlBasePublica = "https://app.talveg.es",
    };

    public static TheoryData<TipoAvisoCorreo> Tipos() => new(Enum.GetValues<TipoAvisoCorreo>());

    [Theory]
    [MemberData(nameof(Tipos))]
    public void El_envoltorio_no_depende_de_imagenes_aunque_haya_URL_publica(TipoAvisoCorreo tipo)
    {
        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca(
            BotonCorreo.Html("Abrir TALVEG", "https://app.talveg.es/"), tipo, OpcionesCompletas, "marta@arcosspa.example");

        html.Should().NotContainEquivalentOf("<img", "la marca es texto: con las imágenes bloqueadas un <img> deja un hueco");
        ProblemasSinImagenes(html).Should().BeEmpty();
        html.Should().Contain("letter-spacing:2.2px;\">TALVEG</div>", "el nombre de la marca se lee como texto en la franja, no solo en el <title>");
    }

    [Fact]
    public void El_fondo_de_la_marca_y_del_cuerpo_va_en_linea_y_como_bgcolor()
    {
        var html = SmtpEmailService.EnvolverEnPlantillaDeMarca("<p>x</p>", TipoAvisoCorreo.Transaccional, OpcionesCompletas);

        html.Should().Contain("bgcolor=\"#122A21\"", "Outlook de escritorio respeta el atributo aunque descarte la hoja de estilos");
        html.Should().Contain("style=\"padding:24px 22px 20px;font-size:14px;color:#2A322E;",
            "el color del texto del cuerpo no puede depender solo del bloque <style>");
        html.Should().Contain("<html lang=\"es\">", "un lector de pantalla necesita saber en qué idioma leer");
    }

    [Fact]
    public void El_boton_es_un_enlace_con_texto_y_la_direccion_va_tambien_escrita()
    {
        const string url = "https://app.talveg.es/cuenta/restablecer-contrasena?userId=1&code=abc";

        var html = BotonCorreo.Html("Restablecer mi contraseña", url);

        html.Should().Contain("<a href=\"https://app.talveg.es/cuenta/restablecer-contrasena?userId=1&amp;code=abc\"");
        html.Should().Contain($">{WebUtility.HtmlEncode("Restablecer mi contraseña")}</a>");
        html.Should().Contain("Si el botón no funciona, copia esta dirección");
        TextoPlanoDeCorreo.DesdeHtml(html).Should().Contain("Restablecer mi contraseña (https://app.talveg.es/cuenta/");
        Regex.Matches(html, Regex.Escape("https://app.talveg.es/cuenta/restablecer-contrasena?userId=1&amp;code=abc"))
            .Should().HaveCount(3, "el enlace del botón, el de la dirección visible y la dirección escrita como texto");
        html.Should().Contain("color:#FFFFFF").And.Contain("bgcolor=\"#122A21\"");
        ProblemasSinImagenes(html).Should().BeEmpty();
    }

    [Fact]
    public void El_boton_codifica_el_texto_y_la_direccion()
    {
        var html = BotonCorreo.Html("<script>", "https://x.example/?a=\"b\"");

        html.Should().NotContain("<script>").And.Contain("&lt;script&gt;");
        html.Should().NotContain("a=\"b\"").And.Contain("a=&quot;b&quot;");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,AAAA")]
    [InlineData("/cuenta/activar")]
    public void El_boton_rechaza_destinos_que_no_son_http_absolutos(string url)
    {
        var accion = () => BotonCorreo.Html("Abrir", url);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void El_texto_plano_decodifica_las_entidades_una_sola_vez()
    {
        const string html = "<p>Empresa &amp;lt;Norte&amp;gt; &amp; Cía</p><p><a href=\"https://x.example/?a=1&amp;amp;b=2\">Ver &amp;lt;ya&amp;gt;</a></p>";

        var texto = TextoPlanoDeCorreo.DesdeHtml(html);

        texto.Should().Contain("Empresa &lt;Norte&gt; & Cía");
        texto.Should().Contain("Ver &lt;ya&gt; (https://x.example/?a=1&amp;b=2)");
    }

    [Fact]
    public void La_salida_sin_expresiones_regulares_quita_etiquetas_y_la_cabecera()
    {
        var texto = TextoPlanoDeCorreo.SinEtiquetas(
            "<html><head><style>.a{color:red}</style><title>t</title></head><body><p>Hola &amp; adiós</p></body></html>");

        texto.Should().NotContain("color").And.NotContain("<").And.NotContain(">");
        texto.Should().Contain("Hola &amp; adiós", "la decodificación de entidades la hace DesdeHtml, una sola vez");
    }

    [Fact]
    public void Un_HTML_con_miles_de_enlaces_sin_cerrar_no_bloquea_ni_impide_el_texto()
    {
        var html = "<p>Hola</p>" + string.Concat(Enumerable.Repeat("<a href=\"https://x.example\">x", 20000));
        var reloj = System.Diagnostics.Stopwatch.StartNew();

        var texto = TextoPlanoDeCorreo.DesdeHtml(html);

        texto.Should().StartWith("Hola");
        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "las expresiones regulares tienen tope de tiempo y hay una vía sin ellas");
    }
    [Fact]
    public void El_mensaje_lleva_parte_de_texto_plano_equivalente_al_HTML()
    {
        const string url = "https://app.talveg.es/cuenta/activar?userId=7&code=xyz";
        var cuerpo = $"<p>Hola Marta,</p>{BotonCorreo.Html("Establecer mi contraseña", url)}<p>El enlace caduca en 60 minutos.</p>";

        var mensaje = SmtpEmailService.ConstruirMensaje(
            "marta@arcosspa.example", "Activa tu acceso a TALVEG", cuerpo, TipoAvisoCorreo.Seguridad,
            null, OpcionesCompletas, NullLogger.Instance);

        mensaje.Body.Should().BeOfType<MultipartAlternative>();
        mensaje.HtmlBody.Should().Contain(WebUtility.HtmlEncode("Establecer mi contraseña"));

        // MimeKit guarda la parte de texto con los saltos de línea canónicos de MIME (CRLF).
        var texto = mensaje.TextBody!.Replace("\r\n", "\n");
        texto.Should().StartWith("TALVEG\n\nCoordinación de actividades empresariales");
        texto.Should().Contain("Hola Marta,");
        texto.Should().Contain($"Establecer mi contraseña ({url})", "sin HTML la llamada a la acción sigue siendo utilizable");
        texto.Should().Contain("El enlace caduca en 60 minutos.");
        texto.Should().Contain("no se puede desactivar", "el pie también llega en texto plano");
        texto.Should().NotContain("<").And.NotContain("&amp;").And.NotContain("{");
    }

    [Fact]
    public void El_texto_plano_sustituye_cada_imagen_por_su_alt_y_quita_estilos()
    {
        const string html = """
            <html><head><style>.x{color:red}</style><title>t</title></head>
            <body><img src="https://x.example/logo.png" alt="Logo de TALVEG"><p>Uno &amp; dos</p>
            <ul><li>Primero</li><li>Segundo</li></ul><p><a href="mailto:info@talveg.es">info@talveg.es</a> · <a href="https://talveg.es">talveg.es</a></p></body></html>
            """;

        var texto = TextoPlanoDeCorreo.DesdeHtml(html);

        texto.Should().Be("Logo de TALVEG\n\nUno & dos\n\n- Primero\n- Segundo\n\ninfo@talveg.es · https://talveg.es");
    }

    /// <summary>
    /// Barrido del código fuente: ninguna plantilla de correo (las que pasan por el envoltorio de marca
    /// y las que salen por Microsoft Graph) puede volver a meter una imagen sin <c>alt</c>, una imagen
    /// de fondo o un enlace sin texto. Mira los ficheros <c>.cs</c> de <c>src</c>, que es donde se
    /// componen todos los cuerpos de correo; las páginas <c>.razor</c> son interfaz, no correo.
    /// </summary>
    [Fact]
    public void Ninguna_plantilla_del_codigo_mete_imagenes_sin_alt_fondos_de_imagen_ni_enlaces_sin_texto()
    {
        var raiz = Path.Combine(RaizDelRepositorio(), "src");
        var ficheros = Directory.EnumerateFiles(raiz, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains("CaeManager.Migrations"))
            .ToList();

        ficheros.Should().Contain(f => f.EndsWith("BotonCorreo.cs"), "el barrido tiene que estar mirando el árbol de src");

        var problemas = ficheros
            .SelectMany(f => ProblemasSinImagenes(File.ReadAllText(f))
                .Select(p => $"{Path.GetRelativePath(raiz, f)}: {p}"))
            .ToList();

        problemas.Should().BeEmpty();
    }

    /// <summary>
    /// Sensibilidad del instrumento: el validador que usan los tests de arriba detecta cada defecto
    /// que dice detectar. Sin esto, un barrido vacío no distinguiría «no hay defectos» de «el
    /// validador no mira».
    /// </summary>
    [Theory]
    [InlineData("""<img src="logo.png">""", "imagen sin alt")]
    [InlineData("""<img src="logo.png" alt="">""", "imagen sin alt")]
    [InlineData("""<img src="logo.png" alt="  ">""", "imagen sin alt")]
    [InlineData("""<td style="background-image:url(x.png)">""", "imagen de fondo")]
    [InlineData("""<td background="x.png">""", "imagen de fondo")]
    [InlineData("""<a href="https://x.example"></a>""", "enlace sin texto")]
    [InlineData("""<a href="https://x.example"> <img src="b.png" alt=""> </a>""", "enlace sin texto")]
    [InlineData("""<a href="">Entrar</a>""", "enlace sin destino")]
    public void El_validador_detecta_cada_defecto(string html, string defecto)
    {
        ProblemasSinImagenes(html).Should().Contain(p => p.StartsWith(defecto));
    }

    [Fact]
    public void El_validador_acepta_lo_correcto()
    {
        ProblemasSinImagenes("""<img src="l.png" alt="TALVEG"><a href="https://x.example">Entrar</a><a href="mailto:{buzon}">{buzon}</a>""")
            .Should().BeEmpty();
    }

    private static List<string> ProblemasSinImagenes(string html)
    {
        var problemas = new List<string>();

        foreach (Match img in Regex.Matches(html, @"<img\s[^>]*>", RegexOptions.IgnoreCase))
        {
            var alt = Regex.Match(img.Value, """\balt\s*=\s*(?:"([^"]*)"|'([^']*)')""", RegexOptions.IgnoreCase);
            if (!alt.Success || string.IsNullOrWhiteSpace(alt.Groups[1].Value + alt.Groups[2].Value))
                problemas.Add($"imagen sin alt: {img.Value}");
        }

        foreach (Match fondo in Regex.Matches(html, @"background(-image)?\s*:[^;""']*url\(|\sbackground\s*=\s*[""'][^""']+[""']", RegexOptions.IgnoreCase))
            problemas.Add($"imagen de fondo: {fondo.Value}");

        foreach (Match a in Regex.Matches(html, """<a\s[^>]*?href\s*=\s*"([^"]*)"[^>]*>(.*?)</a\s*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            if (string.IsNullOrWhiteSpace(a.Groups[1].Value))
                problemas.Add($"enlace sin destino: {a.Value}");

            var textoVisible = Regex.Replace(a.Groups[2].Value, "<[^>]+>", string.Empty);
            if (string.IsNullOrWhiteSpace(textoVisible))
                problemas.Add($"enlace sin texto: {a.Value}");
        }

        return problemas;
    }

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para barrer las plantillas");
        return dir!;
    }
}
