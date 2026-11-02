using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Infrastructure.AsistenteIa;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// MJ-13 (plan de mejora del código, 2026-10-31): las rutas de Anthropic se piden por su constante de
/// <see cref="RutasAnthropic"/>, nunca por un literal.
///
/// <para>
/// <b>Por qué.</b> <c>AnthropicOptions.Para(ruta)</c> hereda el modelo y el esfuerzo globales cuando la ruta no tiene
/// configuración propia: <c>Para("AlertaCritica")</c> con una errata, o una ruta que no figura en <see cref="RutasAnthropic"/>,
/// no falla, sino que usa en silencio el modelo por defecto y esa ruta no se puede afinar (<c>Anthropic__Rutas__…</c>).
/// </para>
///
/// <para>
/// <b>Lo que NO observa</b> (huecos declarados): (1) una variable de tipo <c>string</c> pasada a <c>Para</c> (no es un
/// literal; hoy no hay ninguna); (2) el nombre del receptor no se mira: cualquier <c>.Para("…")</c> con literal en
/// <c>src/CaeManager.Infrastructure</c> cuenta, también si no es de Anthropic (hoy no hay otra); (3) no mide que la
/// ruta tenga configuración en los entornos.
/// </para>
/// </summary>
public class RutasAnthropicTipadasTests
{
    private static readonly Regex ParaConLiteral = new(@"\.\s*Para\s*\(\s*(?:\$|@)?""", RegexOptions.Compiled);

    [Fact]
    public void Ninguna_ruta_de_Anthropic_se_pide_con_un_literal()
    {
        var infractores = FicherosDeInfraestructura()
            .Where(f => ParaConLiteral.IsMatch(LimpiadorDeComentarios.Quitar(File.ReadAllText(f.Absoluta), razor: false)))
            .Select(f => f.Relativa)
            .ToList();

        infractores.Should().BeEmpty(
            "las rutas se piden por RutasAnthropic.<Ruta>; un literal con errata hereda el modelo global sin avisar");
    }

    [Fact]
    public void El_detector_ve_el_literal_y_no_la_constante()
    {
        ParaConLiteral.IsMatch("var r = config.Para(\"AlertaCritica\");").Should().BeTrue();
        ParaConLiteral.IsMatch("var r = config.Para( $\"{x}\");").Should().BeTrue();
        ParaConLiteral.IsMatch("var r = config.Para(RutasAnthropic.AlertaCritica);").Should().BeFalse();
    }

    [Fact]
    public void El_escaner_ve_los_servicios_que_piden_rutas_por_constante()
    {
        // Control positivo: si el recorrido no leyera los servicios de IA, «ningún literal» valdría por vacío.
        var conConstante = FicherosDeInfraestructura()
            .Count(f => File.ReadAllText(f.Absoluta).Contains("Para(RutasAnthropic.", StringComparison.Ordinal));

        conConstante.Should().BeGreaterThan(5);
    }

    [Fact]
    public void Las_constantes_de_rutas_son_distintas_y_no_vacias()
    {
        var valores = typeof(RutasAnthropic).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(c => c.IsLiteral)
            .Select(c => (string)c.GetRawConstantValue()!)
            .ToList();

        valores.Should().OnlyContain(v => !string.IsNullOrWhiteSpace(v));
        valores.Should().OnlyHaveUniqueItems();
        valores.Should().Contain("AlertaCritica");
    }

    private static IEnumerable<(string Absoluta, string Relativa)> FicherosDeInfraestructura()
    {
        var raiz = FuentesDeSrc.RaizDelRepositorio();
        var separador = Path.DirectorySeparatorChar;

        return Directory.EnumerateFiles(Path.Combine(raiz, "src", "CaeManager.Infrastructure"), "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{separador}obj{separador}") && !a.Contains($"{separador}bin{separador}"))
            .Select(a => (a, FuentesDeSrc.Relativa(a)));
    }
}
