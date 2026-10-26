using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

public class RecursosExternosConIntegridadTests
{
    [Fact]
    public void Scripts_y_hojas_de_estilo_externos_declaran_integridad_y_crossorigin()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        var raiz = actual?.FullName ?? throw new InvalidOperationException("No se encontró CaeManager.slnx.");
        var web = Path.Combine(raiz, "src", "CaeManager.Web");
        var extensiones = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".razor", ".cshtml", ".html", ".js" };
        var archivos = Directory.EnumerateFiles(web, "*", SearchOption.AllDirectories)
            .Where(p => extensiones.Contains(Path.GetExtension(p)))
            .Where(p => !Path.GetRelativePath(web, p).Split(Path.DirectorySeparatorChar)
                .Any(segmento => segmento is "bin" or "obj"))
            .ToArray();

        archivos.Should().NotBeEmpty();
        var infracciones = archivos.SelectMany(archivo => EtiquetasSinProteccion(File.ReadAllText(archivo),
                quitarComentarios: !archivo.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            .Select(etiqueta => $"{Path.GetRelativePath(raiz, archivo)}: {etiqueta}"));

        infracciones.Should().BeEmpty("los recursos externos necesitan SRI y CORS anónimo");
    }

    [Theory]
    [InlineData("<script src=\"https://cdn.example/react.js\"></script>", true)]
    [InlineData("<!-- <script src='https://cdn.example/react.js'> -->", false)]
    [InlineData("@* <link rel='stylesheet' href='https://cdn.example/s.css'> *@", false)]
    [InlineData("<a href='https://cdn.example/doc'>x</a><p>https://cdn.example/react.js</p>", false)]
    [InlineData("<SCRIPT defer SRC='//cdn.example/react.js' integrity=''>", true)]
    [InlineData("<script\n src='http://cdn.example/react.js' crossorigin='anonymous'>", true)]
    [InlineData("<script src=https://cdn.example/react.js>", true)]
    [InlineData("<link href='https://cdn.example/style.css' rel='stylesheet'>", true)]
    [InlineData("<script src='/js/app.js'>", false)]
    [InlineData("<script src='@Assets[\"js/app.js\"]'></script>", false)]
    [InlineData("<SCRIPT SRC='HTTPS://cdn.example/react.js'>", true)]
    [InlineData("<script src=' https://cdn.example/react.js'>", true)]
    [InlineData("<script src='\\\\cdn.example/react.js'>", true)]
    [InlineData("<script src='https://cdn.example/r.js' src='/local.js'>", true)]
    [InlineData("<script src='https://cdn.example/r.js' integrity='sha384-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'>", true)]
    [InlineData("<script src='https://cdn.example/r.js' integrity='sha384-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' crossorigin>", false)]
    [InlineData("<script src='https://cdn.example/r.js' integrity='sha384-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' crossorigin=''>", false)]
    [InlineData("<script src='https://cdn.example/r.js' integrity='sha256-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=' crossorigin='anonymous'>", false)]
    [InlineData("<script src='https://cdn.example/r.js' integrity='sha512-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==' crossorigin='anonymous'>", false)]
    [InlineData("<link rel='icon' href='https://cdn.example/icon.png'>", false)]
    [InlineData("<script src='https://cdn.example/react.js' integrity='sha384-AAAA' crossorigin='anonymous'>", true)]
    public void Detecta_recursos_sin_proteccion(string marcado, bool infringe)
    {
        EtiquetasSinProteccion(marcado).Any().Should().Be(infringe);
    }

    [Theory]
    [InlineData("script", "src")]
    [InlineData("link rel='stylesheet'", "href")]
    public void Acepta_integridad_sha384_y_crossorigin_anonimo(string elemento, string atributo)
    {
        var hash = Convert.ToBase64String(new byte[48]);
        var marcado = $"<{elemento} {atributo}='https://cdn.example/recurso' integrity='sha384-{hash}' crossorigin='anonymous'>";
        EtiquetasSinProteccion(marcado).Should().BeEmpty();
        EtiquetasSinProteccion(marcado.Replace("anonymous", "use-credentials")).Should().ContainSingle();
        EtiquetasSinProteccion(marcado.Replace($" integrity='sha384-{hash}'", "")).Should().ContainSingle();
    }

    private static IEnumerable<string> EtiquetasSinProteccion(string marcado, bool quitarComentarios = true)
    {
        // Un recurso dentro de un comentario HTML o de Razor no se carga: no es una infraccion.
        if (quitarComentarios)
            marcado = Regex.Replace(marcado, @"<!--.*?-->|@\*.*?\*@", "", RegexOptions.Singleline);
        foreach (Match etiqueta in Regex.Matches(marcado, @"<(script|link)\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var atributos = Regex.Matches(etiqueta.Value,
                "(?<nombre>[\\w-]+)\\s*=\\s*(?:\"(?<valor>[^\"]*)\"|'(?<valor>[^']*)'|(?<valor>[^\\s>]+))")
                .Cast<Match>().GroupBy(m => m.Groups["nombre"].Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Groups["valor"].Value, StringComparer.OrdinalIgnoreCase);
            var script = etiqueta.Groups[1].Value.Equals("script", StringComparison.OrdinalIgnoreCase);
            if (!script && (!atributos.TryGetValue("rel", out var rel) ||
                !rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("stylesheet", StringComparer.OrdinalIgnoreCase)))
                continue;
            if (!atributos.TryGetValue(script ? "src" : "href", out var url) ||
                !Regex.IsMatch(url.TrimStart(), @"^(https?:)?[/\\]{2}", RegexOptions.IgnoreCase))
                continue;

            var integridadValida = atributos.TryGetValue("integrity", out var integridad) &&
                Regex.IsMatch(integridad, @"(?:^|\s)(sha256-[A-Za-z0-9+/]{43}=|sha384-[A-Za-z0-9+/]{64}|sha512-[A-Za-z0-9+/]{86}==)(?:\s|$)");
            // crossorigin sin valor o vacio equivale a "anonymous" para el navegador.
            var corsAnonimo = atributos.TryGetValue("crossorigin", out var cors)
                ? cors.Length == 0 || cors.Equals("anonymous", StringComparison.OrdinalIgnoreCase)
                : Regex.IsMatch(etiqueta.Value, @"\scrossorigin(?=[\s/>])", RegexOptions.IgnoreCase);
            if (!integridadValida || !corsAnonimo)
                yield return etiqueta.Value;
        }
    }
}
