using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Ningún proyecto del repositorio referencia PDFsharp, PDFsharp-MigraDoc o
/// MigraDoc en una versión igual o superior a 6.5.0.
///
/// <para>
/// <b>Por qué.</b> PDFsharp 6.5.0 (publicada el 2026-10-14) cambió de MIT a
/// AGPL-3.0-only, con licencia comercial de pago aparte. Enlazarla obligaría a
/// publicar el fuente del producto. La 6.2.4 sigue siendo MIT y el cambio no es
/// retroactivo. Dependabot lo tiene bloqueado en <c>.github/dependabot.yml</c>;
/// esta guarda cubre la subida manual o por otra vía. Si la licencia vuelve a
/// ser permisiva o se contrata la comercial, es una decisión de negocio: se
/// retira la regla de Dependabot y este test en la misma PR (§ 23).
/// </para>
///
/// <para>
/// Mira tanto la versión pedida en los <c>.csproj</c>/<c>.props</c> como la
/// resuelta en los <c>packages.lock.json</c>, que es la que se compila.
/// </para>
/// </summary>
public class PdfsharpNoSuperaLaUltimaVersionMitTests
{
    private static readonly Version PrimeraVersionAgpl = new(6, 5, 0);

    private static readonly Regex PaqueteGravado = new(
        @"^(PDFsharp|MigraDoc)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // <PackageReference Include="PDFsharp" Version="6.2.4" /> (atributos en cualquier orden)
    private static readonly Regex ReferenciaDePaquete = new(
        @"<(?:PackageReference|PackageVersion)\b(?<atributos>[^>]*)>", RegexOptions.Compiled);

    private static readonly Regex Atributo = new(
        @"\b(?<nombre>Include|Update|Version)\s*=\s*""(?<valor>[^""]*)""", RegexOptions.Compiled);

    // "PDFsharp": { ... "resolved": "6.2.4" ... } en un packages.lock.json.
    private static readonly Regex EntradaDeLock = new(
        @"""(?<paquete>(?:PDFsharp|MigraDoc)[^""]*)""\s*:\s*\{[^{}]*?""resolved""\s*:\s*""(?<version>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    [Fact]
    public void Ninguna_referencia_a_PDFsharp_ni_MigraDoc_llega_a_la_version_AGPL()
    {
        var raiz = RaizDelRepositorio();
        var infractores = new List<string>();
        var enProyectos = 0;
        var enLocks = 0;

        foreach (var fichero in FicherosDeProyecto(raiz))
        {
            var texto = File.ReadAllText(fichero);
            foreach (Match m in ReferenciaDePaquete.Matches(texto))
            {
                var atributos = Atributo.Matches(m.Groups["atributos"].Value)
                    .ToDictionary(a => a.Groups["nombre"].Value, a => a.Groups["valor"].Value);
                var nombre = atributos.GetValueOrDefault("Include") ?? atributos.GetValueOrDefault("Update");
                if (nombre is null || !PaqueteGravado.IsMatch(nombre)) continue;

                enProyectos++;
                var version = atributos.GetValueOrDefault("Version");
                if (EsAgplOIndeterminada(version))
                    infractores.Add($"{Relativa(raiz, fichero)}: {nombre} {version ?? "(sin versión)"}");
            }
        }

        foreach (var fichero in Directory.EnumerateFiles(raiz, "packages.lock.json", SearchOption.AllDirectories)
                     .Where(f => !EnCarpetaIgnorada(raiz, f)))
        {
            var texto = File.ReadAllText(fichero);
            foreach (Match m in EntradaDeLock.Matches(texto))
            {
                enLocks++;
                var version = m.Groups["version"].Value;
                if (EsAgplOIndeterminada(version))
                    infractores.Add($"{Relativa(raiz, fichero)}: {m.Groups["paquete"].Value} resuelto {version}");
            }
        }

        // Sin esto, un cambio de formato que dejara el recorrido ciego daria verde.
        // Contadores separados: los lock son la unica vigilancia de una subida
        // transitiva, y no deben quedar ciegos amparados por los csproj.
        enProyectos.Should().BeGreaterThan(0,
            "el repositorio referencia PDFsharp en sus .csproj; si no se encuentra, la expresion o la ruta dejaron de ver los ficheros");
        enLocks.Should().BeGreaterThan(0,
            "los packages.lock.json resuelven PDFsharp; si no se encuentra, el formato del lock cambio y la vigilancia quedo ciega");

        infractores.Should().BeEmpty(
            "PDFsharp 6.5.0 y posteriores son AGPL-3.0-only (comercial de pago); la última MIT es la 6.2.4. " +
            "Ver .github/dependabot.yml y la clase de este test antes de subir la versión");
    }

    [Theory]
    [InlineData("6.2.4", false)]
    [InlineData("6.4.9", false)]
    [InlineData("6.5.0", true)]
    [InlineData("6.5.1", true)]
    [InlineData("7.0.0", true)]
    [InlineData("6.5.0-preview-1", true)]
    [InlineData("[6.5.0, )", true)]
    [InlineData("6.*", true)]
    [InlineData(null, true)]
    public void La_deteccion_de_version_distingue_MIT_de_AGPL(string? version, bool esperadoInfractor)
        => EsAgplOIndeterminada(version).Should().Be(esperadoInfractor);

    // Una versión que no se puede leer con certeza (comodín, variable MSBuild,
    // ausente) cuenta como infractora: un comodín decide solo, sin leer la licencia.
    private static bool EsAgplOIndeterminada(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return true;
        var limpia = version.Trim().TrimStart('[', '(', ' ');
        var m = Regex.Match(limpia, @"^(?<v>\d+(?:\.\d+){0,3})(?=$|[-+,\s)\]])");
        if (!m.Success || !Version.TryParse(m.Groups["v"].Value.Contains('.') ? m.Groups["v"].Value : m.Groups["v"].Value + ".0", out var v))
            return true;
        return v >= PrimeraVersionAgpl;
    }

    private static IEnumerable<string> FicherosDeProyecto(string raiz)
        => Directory.EnumerateFiles(raiz, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
                        && !EnCarpetaIgnorada(raiz, f));

    private static bool EnCarpetaIgnorada(string raiz, string fichero)
    {
        var partes = Relativa(raiz, fichero).Split('/');
        return partes.Any(p => p is "bin" or "obj" or "node_modules" or ".git" or ".claude");
    }

    private static string Relativa(string raiz, string fichero)
        => Path.GetRelativePath(raiz, fichero).Replace('\\', '/');

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
                " — este test necesita el árbol fuente del repositorio, no solo los ensamblados compilados.");

        return actual.FullName;
    }
}
