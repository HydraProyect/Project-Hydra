using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Gate permanente de la localización es-ES/ca-ES.
///
/// <para>
/// <b>Paridad de claves</b>: todo recurso neutral (<c>X.resx</c>, español) de
/// <c>src/</c> tiene su <c>X.ca-ES.resx</c> con exactamente las mismas claves, y
/// viceversa. Paridad de <i>claves</i>, no de valores: los valores catalanes
/// empiezan idénticos a los españoles y dejarán de serlo con la traducción.
/// Una clave que falte en ca-ES no rompe nada visible —ResourceManager cae al
/// neutral—, y por eso mismo pasaría inadvertida sin este trinquete.
/// </para>
///
/// <para>
/// <b>Registro en Program.cs</b>: el comportamiento de las opciones lo prueba
/// <c>LocalizacionInfraestructuraTests</c> (Web.Tests) sobre el middleware
/// real; este trinquete solo vigila que <c>Program.cs</c> use esas opciones y
/// no otras, por el mismo motivo que <see cref="AntiforgeryRegistradoEnProgramTests"/>:
/// el proyecto no tiene un <c>WebApplicationFactory</c> que lo observe.
/// </para>
/// </summary>
public class LocalizacionRecursosYRegistroTests
{
    private const string SufijoCatalan = ".ca-ES.resx";

    [Fact]
    public void Cada_recurso_neutral_tiene_su_ca_ES_con_las_mismas_claves()
    {
        var neutrales = RecursosNeutrales();

        // Control positivo: si el patrón de búsqueda dejara de encontrar
        // recursos, la comparación de abajo pasaría en vacío.
        neutrales.Should().Contain(r => r.EndsWith(Path.Combine("Recursos", "TextosComunes.resx")),
            "el instrumento tiene que ver al menos el recurso compartido de Web");

        var discrepancias = new List<string>();
        foreach (var neutral in neutrales)
        {
            var catalan = neutral[..^".resx".Length] + SufijoCatalan;
            if (!File.Exists(catalan))
            {
                discrepancias.Add($"{Relativa(neutral)}: falta {Path.GetFileName(catalan)}");
                continue;
            }

            var clavesNeutral = Claves(neutral);
            var clavesCatalan = Claves(catalan);
            foreach (var clave in clavesNeutral.Except(clavesCatalan))
                discrepancias.Add($"{Relativa(catalan)}: falta la clave «{clave}»");
            foreach (var clave in clavesCatalan.Except(clavesNeutral))
                discrepancias.Add($"{Relativa(catalan)}: sobra la clave «{clave}» (no está en el neutral)");
        }

        discrepancias.Should().BeEmpty();
    }

    [Fact]
    public void Ningun_recurso_ca_ES_queda_huerfano_de_su_neutral()
    {
        var huerfanos = Directory.EnumerateFiles(Path.Combine(RaizDelRepositorio(), "src"), "*" + SufijoCatalan, SearchOption.AllDirectories)
            .Where(EsFuente)
            .Where(c => !File.Exists(c[..^SufijoCatalan.Length] + ".resx"))
            .Select(Relativa)
            .ToList();

        huerfanos.Should().BeEmpty();
    }

    [Fact]
    public void Ningun_recurso_repite_una_clave()
    {
        var repetidas = Directory.EnumerateFiles(Path.Combine(RaizDelRepositorio(), "src"), "*.resx", SearchOption.AllDirectories)
            .Where(EsFuente)
            .SelectMany(r => XDocument.Load(r).Root!.Elements("data")
                .GroupBy(d => (string)d.Attribute("name")!)
                .Where(g => g.Count() > 1)
                .Select(g => $"{Relativa(r)}: «{g.Key}»"))
            .ToList();

        repetidas.Should().BeEmpty();
    }

    [Fact]
    public void Program_registra_la_localizacion_por_cuenta_antes_de_autenticar()
    {
        var program = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        program.Should().Contain("builder.Services.AddLocalization();",
            "sin AddLocalization no hay IStringLocalizerFactory aunque las culturas estén configuradas");
        program.Should().Contain("Configure<RequestLocalizationOptions>(CulturaUsuarioCookie.ConfigurarLocalizacion)",
            "las opciones que prueba LocalizacionInfraestructuraTests son las únicas de la aplicación");
        program.Should().Contain("app.MapIdiomaEndpoints();");

        var usoLocalizacion = program.IndexOf("app.UseRequestLocalization(", StringComparison.Ordinal);
        usoLocalizacion.Should().BeGreaterThan(-1);
        program.IndexOf("app.UseRequestLocalization(", usoLocalizacion + 1, StringComparison.Ordinal)
            .Should().Be(-1, "un segundo UseRequestLocalization con otras opciones pisaría las de la cuenta");
        usoLocalizacion.Should().BeLessThan(program.IndexOf("app.UseAuthentication();", StringComparison.Ordinal),
            "la cultura se resuelve antes que el resto del pipeline, incluidas las respuestas de autenticación");
    }

    private static List<string> RecursosNeutrales() =>
        Directory.EnumerateFiles(Path.Combine(RaizDelRepositorio(), "src"), "*.resx", SearchOption.AllDirectories)
            .Where(EsFuente)
            // Neutral = sin sufijo de cultura: "X.resx", no "X.ca-ES.resx".
            .Where(r => !Path.GetFileNameWithoutExtension(r).Contains('.'))
            .ToList();

    private static HashSet<string> Claves(string resx) =>
        XDocument.Load(resx).Root!.Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet();

    private static bool EsFuente(string ruta) =>
        !ruta.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(parte => parte is "bin" or "obj");

    private static string Relativa(string ruta) => Path.GetRelativePath(RaizDelRepositorio(), ruta);

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
            " — este test necesita el árbol fuente del repositorio.");
    }
}
