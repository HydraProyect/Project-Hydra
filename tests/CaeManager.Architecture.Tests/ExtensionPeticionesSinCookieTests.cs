using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Toda petición de la extensión al servidor viaja sin cookies del navegador.</b> La
/// extensión se autentica con su credencial propia (esquema de extensión); si <c>fetch</c>
/// adjuntara además la cookie de sesión de Identity, una página con la sesión abierta
/// podría ejercer autoridad de sesión desde la extensión y la política
/// <c>SesionOExtension</c> dejaría de distinguir quién manda. Se vigila el texto:
/// cada <c>fetch(</c> de <c>extension/*.js</c> (fuera de comentarios) lleva
/// <c>credentials: "omit"</c>. No ejecuta la extensión: eso lo cubre el E2E
/// <c>TokenDeExtensionSinCookieTests</c>.
/// </summary>
public class ExtensionPeticionesSinCookieTests
{
    [Fact]
    public void Cada_fetch_de_la_extension_omite_las_credenciales_del_navegador()
    {
        var raiz = RaizDelRepositorio();
        var ficheros = Directory.EnumerateFiles(Path.Combine(raiz, "extension"), "*.js", SearchOption.AllDirectories).ToList();

        var fetches = 0;
        var omisiones = 0;
        foreach (var fichero in ficheros)
        {
            var codigo = File.ReadAllLines(fichero)
                .Select(l => l.TrimStart())
                .Where(l => l.Length > 0 && !l.StartsWith("//") && !l.StartsWith("/*") && !l.StartsWith('*'))
                .ToList();
            fetches += codigo.Sum(l => Contar(l, "fetch("));
            omisiones += codigo.Sum(l => Contar(l, "credentials: \"omit\""));
        }

        fetches.Should().BeGreaterThan(0, "control positivo: la extensión hace al menos una petición al servidor");
        omisiones.Should().Be(fetches,
            "cada fetch de la extensión debe llevar `credentials: \"omit\"`; sin ello el navegador adjunta la cookie de sesión");
    }

    private static int Contar(string texto, string patron)
    {
        var total = 0;
        for (var i = texto.IndexOf(patron, StringComparison.Ordinal); i >= 0; i = texto.IndexOf(patron, i + patron.Length, StringComparison.Ordinal))
            total++;
        return total;
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
