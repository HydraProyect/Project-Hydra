using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Email;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
            .SelectMany(f => ProblemasEnCodigo(File.ReadAllText(f))
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
    [InlineData("""<a href=''>Entrar</a>""", "enlace sin destino")]
    [InlineData("""<a href=>Entrar</a>""", "enlace sin destino")]
    [InlineData("""<a href=" ">Entrar</a>""", "enlace sin destino")]
    [InlineData("""<a>Entrar</a>""", "enlace sin destino")]
    [InlineData("""<a name="inicio">Entrar</a>""", "enlace sin destino")]
    [InlineData("""<A HREF=''>Entrar</A>""", "enlace sin destino")]
    [InlineData("""<a href='https://x.example'></a>""", "enlace sin texto")]
    [InlineData("""<a href=https://x.example> </a>""", "enlace sin texto")]
    [InlineData("""<a href='https://x.example'><img src='b.png' alt=''></a>""", "enlace sin texto")]
    [InlineData("""<img src='logo.png'>""", "imagen sin alt")]
    [InlineData("""<img src=logo.png alt=>""", "imagen sin alt")]
    [InlineData("""<td background=x.png>""", "imagen de fondo")]
    [InlineData("""<td background='x.png'>""", "imagen de fondo")]
    public void El_validador_detecta_cada_defecto(string html, string defecto)
    {
        ProblemasSinImagenes(html).Should().Contain(p => p.StartsWith(defecto));
    }

    [Theory]
    [InlineData("""<a href='https://x.example'>Entrar</a>""")]
    [InlineData("""<a href=https://x.example>Entrar</a>""")]
    [InlineData("""<a class="b" href = 'mailto:info@talveg.es' >info@talveg.es</a>""")]
    [InlineData("""<img src='l.png' alt='TALVEG'>""")]
    [InlineData("""<img src=l.png alt=TALVEG>""")]
    public void El_validador_acepta_comillas_simples_dobles_y_ausentes(string html)
    {
        ProblemasSinImagenes(html).Should().BeEmpty();
    }

    [Fact]
    public void El_validador_no_se_deja_tragar_el_resto_del_fichero_por_un_style_o_un_script()
    {
        var problemas = ProblemasSinImagenes("""<style>.a{color:red}</style><script>var x = 1;</script><title>t</title><a href=''>Entrar</a>""");

        problemas.Should().Contain(p => p.StartsWith("enlace sin destino"));
    }

    [Theory]
    [InlineData("""<p><a href=https://ejemplo.es>Entrar</a></p>""", "Entrar (https://ejemplo.es)")]
    [InlineData("""<p><a href='https://ejemplo.es/x?a=1'>Entrar</a></p>""", "Entrar (https://ejemplo.es/x?a=1)")]
    [InlineData("""<p><a class=boton href=https://ejemplo.es/x style="color:#fff">Entrar</a></p>""", "Entrar (https://ejemplo.es/x)")]
    [InlineData("""<p><a href="https://ejemplo.es/x">Entrar</a></p>""", "Entrar (https://ejemplo.es/x)")]
    [InlineData("""<p><a href=mailto:info@talveg.es>Escríbenos</a></p>""", "Escríbenos (info@talveg.es)")]
    [InlineData("""<p><img src=l.png alt=Logo><img src='m.png' alt='Marca'></p>""", "LogoMarca")]
    [InlineData("""<p><a data-href=incorrecto href=https://ejemplo.es>Entrar</a></p>""", "Entrar (https://ejemplo.es)")]
    [InlineData("""<p><a data-href="incorrecto" href="https://ejemplo.es">Entrar</a></p>""", "Entrar (https://ejemplo.es)")]
    [InlineData("""<p><a data-href=incorrecto>Entrar</a></p>""", "Entrar")]
    [InlineData("""<p><a class="x"href=https://ejemplo.es>Entrar</a></p>""", "Entrar (https://ejemplo.es)")]
    [InlineData("""<p><img src=l.png data-alt=incorrecto alt=Logo></p>""", "Logo")]
    [InlineData("""<p><img src=l.png data-alt=incorrecto></p>""", "")]
    [InlineData("""<p><img src=l.png data-alt="incorrecto" alt="Logo"></p>""", "Logo")]
    [InlineData("""<p><a class='x'href=https://ejemplo.es>Entrar</a></p>""", "Entrar (https://ejemplo.es)")]
    [InlineData("""<p><a/href=https://ejemplo.es>Entrar</a></p>""", "Entrar (https://ejemplo.es)")]
    [InlineData("<p><a class=x\nhref=https://ejemplo.es>Entrar</a></p>", "Entrar (https://ejemplo.es)")]
    [InlineData("""<p><A CLASS=x HREF=https://ejemplo.es>Entrar</A></p>""", "Entrar (https://ejemplo.es)")]
    [InlineData("""<p><img src='l.png'alt='Logo'></p>""", "Logo")]
    [InlineData("<p><img src=l.png\nalt=Logo></p>", "Logo")]
    [InlineData("""<p><IMG SRC=l.png ALT=Logo></p>""", "Logo")]
    [InlineData("""<p><img src=l.png aria-alt=incorrecto alt=Logo></p>""", "Logo")]
    public void El_texto_plano_conserva_la_direccion_con_comillas_dobles_simples_o_sin_comillas(string html, string esperado)
    {
        TextoPlanoDeCorreo.DesdeHtml(html).Should().Be(esperado);
    }

    /// <summary>
    /// Sensibilidad del barrido completo (Roslyn + validador), no solo del validador: un defecto dentro
    /// de un literal de cadena se detecta aunque lo rodee algo que parezca un comentario o una regex.
    /// </summary>
    [Theory]
    [InlineData("""""
        class C { const string H = $$"""
            <p>Hola</p>
            // <a href="">Entrar</a>
            <p>Adiós</p>
            """; }
        """"", "enlace sin destino")]
    [InlineData("""""
        class C { const string H = @"<p>Hola</p>
        // <a href="""">Entrar</a>
        <p>Adiós</p>"; }
        """"", "enlace sin destino")]
    [InlineData("""class C { const string H = "<p>(?<a href=\"\">Entrar</a>)</p>"; }""", "enlace sin destino")]
    [InlineData("""""class C { const string H = @"(?<a href="""">Entrar</a>)"; }""""", "enlace sin destino")]
    [InlineData("""class C { const string H = "<p>(?<a>Entrar</a>)</p>"; }""", "enlace sin destino")]
    [InlineData("""class C { const string H = @"(?<a>Entrar</a>)"; }""", "enlace sin destino")]
    [InlineData("""class C { string M(string t) => Regex.Replace(t, "x", "<a>Entrar</a>"); }""", "enlace sin destino")]
    [InlineData("""class C { string M(Regex r, string t) => r.Replace(t, "(?<a>Entrar</a>)"); }""", "enlace sin destino")]
    [InlineData("""class C { bool M(string t) => Regex.IsMatch("(?<a>Entrar</a>)", t); }""", "enlace sin destino")]
    [InlineData("""class C { string M(string t, string p) => Regex.Replace(t, replacement: "<a>Entrar</a>", pattern: p); }""", "enlace sin destino")]
    [InlineData("""class C { bool M(string p) => Regex.IsMatch(pattern: p, input: "<a>Entrar</a>"); }""", "enlace sin destino")]
    [InlineData("""class C { const string P = "(?<a>Entrar</a>)"; static readonly Regex R = new(P); }""", "enlace sin destino")]
    [InlineData("""class C { const string H = "<a href=\"\">" + "Entrar</a>"; }""", "enlace sin destino")]
    [InlineData("""class C { string M(string u) => "<a href=\"" + "\">Entrar</a>"; }""", "enlace sin destino")]
    [InlineData("""class C { string M(string u) => $"<a href=\"{u}\"></a>"; }""", "enlace sin texto")]
    [InlineData("""class C { string M(string u) => $"<p>{(u.Length > 0 ? "<img src='x.png'>" : "")}</p>"; }""", "imagen sin alt")]
    [InlineData("""class C { const string H = "<td style=\"background-image:url(x.png)\">"; }""", "imagen de fondo")]
    [InlineData("""class C { const string H = "<style>.a{color:red}</style><a href=\"\">Entrar</a>"; }""", "enlace sin destino")]
    public void El_barrido_detecta_defectos_dentro_de_literales_de_cadena(string codigo, string defecto)
    {
        ProblemasEnCodigo(codigo).Should().Contain(p => p.StartsWith(defecto));
    }

    /// <summary>
    /// Lo que no es plantilla no da falso positivo: comentarios reales (de línea, de bloque, de
    /// documentación), grupos de regex válidos y plantillas correctas con huecos interpolados.
    /// </summary>
    [Theory]
    [InlineData("""
        class C
        {
            // <a href="">Entrar</a> es un comentario
            /* <a href="">Entrar</a> */
            /// <summary>Se pinta como <a href=""></a></summary>
            int X;
        }
        """)]
    [InlineData("""class C { static readonly System.Text.RegularExpressions.Regex R = new(@"(?<a>\d+)(?<!x)(?<=y)(?<n-m>z)(?<href>[^""]*)"); }""")]
    [InlineData("""class C { bool M(string t) => System.Text.RegularExpressions.Regex.IsMatch(t, "<a\\b[^>]*?\\bhref\\s*=\\s*(?:\"(?<href>[^\"]*)\")[^>]*>(?<texto>.*?)</a\\s*>"); }""")]
    [InlineData("""class C { static readonly Regex R = new("(?<a>Entrar</a>)"); }""")]
    [InlineData("""class C { static Regex R { get; } = new("(?<a>Entrar</a>)"); }""")]
    [InlineData("""class C { static readonly Regex R = new Regex(("(?<a>Entrar</a>)")); }""")]
    [InlineData("""class C { static readonly Regex R = new Regex(pattern: "(?<a>Entrar</a>)"); }""")]
    [InlineData("""class C { object M(string t) => new object[] { Regex.Match(t, "(?<a>Entrar</a>)"), Regex.Matches(t, "(?<a>Entrar</a>)"), Regex.Split(t, "(?<a>Entrar</a>)"), Regex.EnumerateMatches(t, "(?<a>Entrar</a>)"), Regex.Count(t, "(?<a>Entrar</a>)") }; }""")]
    [InlineData("""class C { string M(string t) => Regex.Replace(t, replacement: "<a href=\"x\">Ver</a>", pattern: "(?<a>Entrar</a>)"); }""")]
    [InlineData("""class C { [GeneratedRegexAttribute("(?<a>Entrar</a>)")] static partial Regex R(); }""")]
    [InlineData("""class C { [GeneratedRegex(pattern: "(?<a>Entrar</a>)")] static partial Regex R(); }""")]
    [InlineData("""class C { static readonly Regex R = new Regex("<p>" + "(?<a>Entrar</a>)", RegexOptions.None); }""")]
    [InlineData("""class C { bool M(string t) => Regex.IsMatch(t, "(?<a>Entrar</a>)"); }""")]
    [InlineData("""class C { bool M(string t) => Regex.IsMatch(t, pattern: "(?<a>Entrar</a>)"); }""")]
    [InlineData("""class C { string M(string t) => Regex.Replace(t, "(?<a>Entrar</a>)", "<a href=\"x\">Ver</a>"); }""")]
    [InlineData("""class C { [GeneratedRegex("(?<a>Entrar</a>)")] static partial Regex R(); }""")]
    [InlineData("""class C { [System.Text.RegularExpressions.GeneratedRegex("(?<a>Entrar</a>)", RegexOptions.None)] static partial Regex R(); }""")]
    [InlineData("""class C { string M(string u, string t) => $"<a href=\"{u}\">{t}</a>" + "<img src='l.png' alt='TALVEG'>"; }""")]
    [InlineData("""class C { int M(int a, int b) => a + b + 1; }""")]
    public void El_barrido_no_da_falsos_positivos_con_comentarios_ni_regex_reales(string codigo)
    {
        ProblemasEnCodigo(codigo).Should().BeEmpty();
    }

    [Fact]
    public void El_validador_acepta_lo_correcto()
    {
        ProblemasSinImagenes("""<img src="l.png" alt="TALVEG"><a href="https://x.example">Entrar</a><a href="mailto:{buzon}">{buzon}</a>""")
            .Should().BeEmpty();
    }

    /// <summary>
    /// Las imágenes y los enlaces se leen con un analizador HTML (AngleSharp), no con expresiones
    /// regulares: así valen comillas dobles, simples o ausentes, y un <c>&lt;a&gt;</c> sin
    /// <c>href</c> o con <c>href</c> vacío es un fallo. Un literal de plantilla puede contener
    /// <c>&lt;style&gt;</c>, <c>&lt;script&gt;</c> o <c>&lt;title&gt;</c>, que el analizador tomaría
    /// como texto sin etiquetas y se tragaría el resto del literal; por eso
    /// esas aperturas se renombran antes de analizar. Las imágenes de fondo siguen con expresión
    /// regular porque un <c>&lt;td background&gt;</c> suelto, sin tabla, el analizador lo descarta.
    /// </summary>
    private static List<string> ProblemasSinImagenes(string html)
    {
        var problemas = new List<string>();

        foreach (Match fondo in Regex.Matches(html, @"background(-image)?\s*:[^;""']*url\(|\sbackground\s*=\s*(?:""[^""]+""|'[^']+'|[^\s>""']+)", RegexOptions.IgnoreCase))
            problemas.Add($"imagen de fondo: {fondo.Value}");

        var neutralizado = Regex.Replace(
            html, @"<(/?)(script|style|textarea|title|xmp|iframe|noembed|noframes|noscript|plaintext)\b", "<$1x-$2", RegexOptions.IgnoreCase);
        var documento = new HtmlParser().ParseDocument(neutralizado);

        foreach (var img in documento.QuerySelectorAll("img"))
        {
            if (string.IsNullOrWhiteSpace(img.GetAttribute("alt")))
                problemas.Add($"imagen sin alt: {img.OuterHtml}");
        }

        foreach (var a in documento.QuerySelectorAll("a"))
        {
            if (string.IsNullOrWhiteSpace(a.GetAttribute("href")))
                problemas.Add($"enlace sin destino: {a.OuterHtml}");

            if (string.IsNullOrWhiteSpace(a.TextContent))
                problemas.Add($"enlace sin texto: {a.OuterHtml}");
        }

        return problemas;
    }

    /// <summary>
    /// Valida un fichero C# sin leerlo como si fuera HTML: se analiza con Roslyn y solo se pasan al
    /// validador los literales de cadena (normales, verbatim, crudas, interpoladas y concatenaciones de
    /// ellas; cada hueco <c>{...}</c> o operando no literal se sustituye por <c>{}</c>). Los comentarios
    /// (<c>//</c>, <c>/* */</c>, <c>///</c>) son trivia y no llegan nunca; una línea <c>//</c> dentro de
    /// una cadena multilínea sí es contenido del literal y se valida.
    /// </summary>
    private static List<string> ProblemasEnCodigo(string codigo)
    {
        var raiz = CSharpSyntaxTree.ParseText(codigo).GetRoot();
        var problemas = new List<string>();

        foreach (var nodo in raiz.DescendantNodes().OfType<ExpressionSyntax>())
        {
            if (!nodo.IsKind(SyntaxKind.StringLiteralExpression)
                && !nodo.IsKind(SyntaxKind.InterpolatedStringExpression)
                && !nodo.IsKind(SyntaxKind.AddExpression))
                continue;

            // Una concatenación se valida entera desde su raíz, no por trozos.
            SyntaxNode? padre = nodo.Parent;
            while (padre is ParenthesizedExpressionSyntax)
                padre = padre.Parent;
            if (padre.IsKind(SyntaxKind.AddExpression))
                continue;

            // El patrón de una expresión regular no es una plantilla: sus grupos (?<a>…) no son etiquetas.
            if (EsPatronDeRegex(nodo))
                continue;

            var (texto, hayLiteral) = TextoDeLaExpresion(nodo);
            if (hayLiteral)
                problemas.AddRange(ProblemasSinImagenes(texto));
        }

        return problemas;
    }

    private static (string Texto, bool HayLiteral) TextoDeLaExpresion(ExpressionSyntax expresion)
    {
        switch (expresion)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                return (literal.Token.ValueText, true);
            case InterpolatedStringExpressionSyntax interpolada:
                return (string.Concat(interpolada.Contents.Select(c =>
                    c is InterpolatedStringTextSyntax texto ? texto.TextToken.ValueText : "{}")), true);
            case ParenthesizedExpressionSyntax parentesis:
                return TextoDeLaExpresion(parentesis.Expression);
            case BinaryExpressionSyntax suma when suma.IsKind(SyntaxKind.AddExpression):
                var (izquierda, literalIzquierda) = TextoDeLaExpresion(suma.Left);
                var (derecha, literalDerecha) = TextoDeLaExpresion(suma.Right);
                return (izquierda + derecha, literalIzquierda || literalDerecha);
            default:
                return ("{}", false);
        }
    }

    private static readonly HashSet<string> MetodosEstaticosDeRegex =
        ["Match", "IsMatch", "Replace", "Matches", "Split", "EnumerateMatches", "Count"];

    /// <summary>
    /// Un literal es de verdad una expresión regular, y no una plantilla HTML, cuando es el patrón de
    /// <c>new Regex(...)</c> (o de <c>new(...)</c> con tipo <c>Regex</c> declarado), el segundo argumento
    /// (<c>pattern</c>) de <c>Regex.Match/IsMatch/Replace/Matches/Split/…</c>, o el argumento de
    /// <c>[GeneratedRegex(...)]</c>. Se decide por el lugar sintáctico del literal, no por su forma: un
    /// texto con <c>(?&lt;a&gt;Entrar&lt;/a&gt;)</c> en un literal normal es HTML y se valida. El
    /// reemplazo de <c>Regex.Replace</c> y cualquier otro argumento siguen siendo plantilla.
    /// <para>
    /// Límite declarado: un patrón que no esté en uno de esos lugares (una constante <c>const string P</c>
    /// que luego se pasa a un <c>Regex</c>, o un <c>return "…"</c>) se valida como HTML y, si contiene un
    /// <c>(?&lt;a&gt;…)</c>, da un falso positivo. Es el lado seguro: preferimos un falso positivo que se
    /// corrige pasando el patrón directo al <c>Regex</c> a un falso negativo que esconda un enlace roto.
    /// Hoy ningún fichero de <c>src</c> está en ese caso.
    /// </para>
    /// </summary>
    private static bool EsPatronDeRegex(ExpressionSyntax expresion)
    {
        SyntaxNode nodo = expresion;
        while (nodo.Parent is ParenthesizedExpressionSyntax)
            nodo = nodo.Parent;

        if (nodo.Parent is AttributeArgumentSyntax { Parent: AttributeArgumentListSyntax { Parent: AttributeSyntax atributo } listaAtributo } argumentoDeAtributo)
            return NombreFinal(atributo.Name) is "GeneratedRegex" or "GeneratedRegexAttribute"
                   && argumentoDeAtributo.NameEquals is null
                   && EsElPatron(argumentoDeAtributo.NameColon, listaAtributo.Arguments.IndexOf(argumentoDeAtributo), 0);

        if (nodo.Parent is not ArgumentSyntax argumento || argumento.Parent is not ArgumentListSyntax lista)
            return false;

        var posicion = lista.Arguments.IndexOf(argumento);

        switch (lista.Parent)
        {
            case ObjectCreationExpressionSyntax creacion:
                return NombreFinal(creacion.Type) == "Regex" && EsElPatron(argumento.NameColon, posicion, 0);
            case ImplicitObjectCreationExpressionSyntax implicita:
                return TipoDeclaradoDe(implicita) == "Regex" && EsElPatron(argumento.NameColon, posicion, 0);
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax acceso }:
                return NombreFinal(acceso.Expression) == "Regex"
                       && MetodosEstaticosDeRegex.Contains(acceso.Name.Identifier.Text)
                       && EsElPatron(argumento.NameColon, posicion, 1);
            default:
                return false;
        }
    }

    // Con argumento con nombre se decide solo por el nombre (pattern:); sin nombre, por la posición.
    private static bool EsElPatron(NameColonSyntax? nombre, int posicion, int posicionDelPatron) =>
        nombre is not null ? nombre.Name.Identifier.Text == "pattern" : posicion == posicionDelPatron;

    private static string? TipoDeclaradoDe(ImplicitObjectCreationExpressionSyntax creacion) =>
        creacion.Parent?.Parent switch
        {
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaracion } => NombreFinal(declaracion.Type),
            PropertyDeclarationSyntax propiedad => NombreFinal(propiedad.Type),
            _ => null,
        };

    private static string? NombreFinal(SyntaxNode? nodo) => nodo switch
    {
        IdentifierNameSyntax identificador => identificador.Identifier.Text,
        GenericNameSyntax generico => generico.Identifier.Text,
        QualifiedNameSyntax cualificado => NombreFinal(cualificado.Right),
        AliasQualifiedNameSyntax alias => NombreFinal(alias.Name),
        MemberAccessExpressionSyntax acceso => NombreFinal(acceso.Name),
        _ => null,
    };

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para barrer las plantillas");
        return dir!;
    }
}
