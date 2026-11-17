using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// El ámbito de petición de Inbound (<c>AmbitoPeticionInbound</c>, B-03) abre el alcance de datos con papel Inbound: si
/// cualquier componente lo abriera, un lector de Outbound del Tenant TCE obtendría el alcance de Inbound. Solo lo fija
/// <c>AmbitoInboundBehavior</c>, en el pipeline, alrededor de cada petición (revisor-puente Opus, ronda 2 de B-03), como
/// <c>LlamadasAAmbitoTenantExplicitoCongeladasTests</c> hace con el ámbito explícito. Mide el texto de <c>src</c> sin
/// comentarios; no ve una llamada por reflexión.
/// </summary>
public class AmbitoPeticionInboundSoloLoFijaElBehaviorTests
{
    private static readonly Regex Llamada = new(@"\bAmbitoPeticionInbound\s*\.\s*(Abrir|Fijar)\s*\(", RegexOptions.Compiled);
    private static readonly Regex Comentario = new(@"//[^\n]*|/\*[\s\S]*?\*/", RegexOptions.Compiled);

    [Fact]
    public void Solo_el_behavior_abre_o_fija_el_ambito_de_Inbound()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        dir.Should().NotBeNull("control: el escaneo encuentra src");

        var llamadores = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Llamada.IsMatch(Comentario.Replace(File.ReadAllText(f), string.Empty)))
            .Select(f => Path.GetFileName(f))
            .ToList();

        llamadores.Should().Equal(["AmbitoPeticionInbound.cs"],
            "el ámbito de Inbound solo lo fija AmbitoInboundBehavior, definido junto al propio ámbito");
    }

    [Fact]
    public void El_instrumento_ve_las_llamadas()
    {
        Llamada.IsMatch("using (AmbitoPeticionInbound.Abrir())").Should().BeTrue();
        Llamada.IsMatch("AmbitoPeticionInbound.Fijar(true)").Should().BeTrue();
        Llamada.IsMatch("AmbitoPeticionInbound.Activo").Should().BeFalse();
    }
}
