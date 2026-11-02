using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>page.WaitForLoadStateAsync</c> puede quedarse esperando un evento que ya pasó (#1713; el porqué, en
/// <c>Ayudas.EsperarCargaAsync</c>): en la suite E2E solo la llama ese ayudante. El fallo es raro, así que una llamada
/// directa nueva no se vería en CI hasta expulsar una PR de la cola de fusión.
///
/// <para>
/// <b>Contrato efectivo.</b> El detector es sintáctico: cuenta las invocaciones cuyo miembro se llama
/// <c>WaitForLoadStateAsync</c> o <c>WaitForNavigationAsync</c>. NO ve el grupo de método pasado como delegado, ni
/// <c>WaitForURLAsync</c>, que con la URL ya alcanzada acaba en la misma espera: esa tiene su propia advertencia en la guía de
/// <c>EsperasFijasDeE2ECongeladasTests</c> y no está cubierta aquí.
/// </para>
/// </summary>
public class EsperasDeCargaPorElAyudanteTests
{
    private const string FicheroDelAyudante = "tests/CaeManager.E2ETests/Ayudas.cs";
    private const int LlamadasDelAyudante = 2;

    private static int LlamadasDirectas(string texto) =>
        CSharpSyntaxTree.ParseText(texto).GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Count(i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "WaitForLoadStateAsync" or "WaitForNavigationAsync",
            });

    [Fact]
    public void Solo_el_ayudante_llama_a_las_esperas_de_carga_de_Playwright()
    {
        var fuentes = AnalisisDeSuiteE2E.LeerE2E();
        fuentes.Should().HaveCountGreaterThan(50, "si baja de golpe, el escáner dejó de mirar la suite");

        var directas = fuentes
            .Select(f => (f.Ruta, Llamadas: LlamadasDirectas(f.Texto)))
            .Where(f => f.Llamadas > 0)
            .ToDictionary(f => f.Ruta, f => f.Llamadas);

        directas.Should().Equal(
            new Dictionary<string, int> { [FicheroDelAyudante] = LlamadasDelAyudante },
            "una llamada directa puede perder el evento y agotar el plazo con la página ya cargada: usa Ayudas.EsperarCargaAsync");
    }

    [Theory]
    [InlineData("await page.WaitForLoadStateAsync(LoadState.Load);", 1)]
    [InlineData("await pagina.WaitForLoadStateAsync();", 1)]
    [InlineData("await contexto.Pages[0].WaitForLoadStateAsync(LoadState.NetworkIdle);", 1)]
    [InlineData("await page.WaitForNavigationAsync();", 1)]
    [InlineData("await Ayudas.EsperarCargaAsync(page, LoadState.Load);", 0)]
    [InlineData("// await page.WaitForLoadStateAsync(LoadState.Load);", 0)]
    public void El_detector_ve_la_llamada_directa(string cuerpo, int esperadas)
    {
        LlamadasDirectas($"class T {{ async Task M(dynamic page, dynamic pagina, dynamic contexto) {{ {cuerpo} }} }}").Should().Be(esperadas);
    }
}
