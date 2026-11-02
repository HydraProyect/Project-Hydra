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
/// <b>Lo que exige.</b> Toda llamada <c>.Para(</c> de <c>src/CaeManager.Infrastructure</c> pasa como argumento un
/// miembro de <c>RutasAnthropic</c>: un literal, una variable, una constante local, un argumento con nombre o una
/// condicional dejan de pasar (hoy hay nueve llamadas y todas son de esa forma).
///
/// <b>Lo que NO observa</b> (huecos declarados): (1) el chequeo es textual, no de tipos: un <c>.Para(</c> legítimo de otra
/// clase en Infrastructure habría que justificarlo aquí (hoy no hay ninguno); (2) no mide que la ruta tenga
/// configuración en los entornos; (3) ningún test fija que cada servicio pida SU ruta (cambiar una constante por otra
/// pasa en verde).
/// </para>
/// </summary>
public class RutasAnthropicTipadasTests
{
    private static readonly Regex LlamadaPara = new(@"\.\s*Para\s*\(", RegexOptions.Compiled);

    private static readonly Regex LlamadaTipada = new(@"\.\s*Para\s*\(\s*RutasAnthropic\s*\.\s*[A-Za-z]\w*\s*\)", RegexOptions.Compiled);

    private static bool TodasTipadas(string texto) =>
        LlamadaPara.Matches(texto).Count == LlamadaTipada.Matches(texto).Count;

    [Fact]
    public void Ninguna_ruta_de_Anthropic_se_pide_con_un_literal()
    {
        var infractores = FicherosDeInfraestructura()
            .Where(f => !TodasTipadas(LimpiadorDeComentarios.Quitar(File.ReadAllText(f.Absoluta), razor: false)))
            .Select(f => f.Relativa)
            .ToList();

        infractores.Should().BeEmpty(
            "las rutas se piden por RutasAnthropic.<Ruta>; un literal con errata hereda el modelo global sin avisar");
    }

    [Fact]
    public void El_detector_ve_el_literal_y_no_la_constante()
    {
        TodasTipadas("var r = config.Para(RutasAnthropic.AlertaCritica);").Should().BeTrue();
        TodasTipadas("var r = config.Para(\"AlertaCritica\");").Should().BeFalse();
        TodasTipadas("var r = config.Para( $\"{x}\");").Should().BeFalse();
        TodasTipadas("var r = config.Para(ruta: \"AlertaCritca\");").Should().BeFalse();
        TodasTipadas("var r = config.Para(cond ? \"A\" : \"B\");").Should().BeFalse();
        TodasTipadas("const string n = \"X\"; var r = config.Para(n);").Should().BeFalse();
        TodasTipadas("var r = config.Para(RutasAnthropic.Ocr ?? \"Y\");").Should().BeFalse();
    }

    [Fact]
    public void El_escaner_ve_los_servicios_que_piden_rutas_por_constante()
    {
        // Control positivo: si el recorrido no leyera los servicios de IA, «ningún literal» valdría por vacío.
        var conConstante = FicherosDeInfraestructura()
            .Count(f => LlamadaTipada.IsMatch(LimpiadorDeComentarios.Quitar(File.ReadAllText(f.Absoluta), razor: false)));

        conConstante.Should().BeGreaterThanOrEqualTo(7);
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
