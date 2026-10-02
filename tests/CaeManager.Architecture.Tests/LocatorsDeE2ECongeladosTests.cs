using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>S6: contrato de localizadores de E2E, solo congelar</b> — análisis de causas raíz de
/// 2026-10-02 (<c>Project-Hydra-Negocio/tecnico/evaluacion/</c>), clase C10: tres hallazgos de
/// gravedad ALTA del revisor-puente del lote UX fueron un E2E o un test de integración atado a un
/// rótulo. <c>GetByTestId</c> no se usaba en ningún sitio el 2026-10-02 porque los componentes de Web no
/// emiten <c>data-testid</c>; ese es el trabajo de S2a/S12 y no de este instrumento.
///
/// <para>
/// <b>Qué hace.</b> Congela, por fichero de <c>tests/CaeManager.E2ETests</c>, cuántos localizadores
/// dependen del texto visible o de la posición (<see cref="AnalisisDeLocatorsE2E"/>). Un E2E nuevo con
/// <c>GetByText</c>, <c>.First</c>, <c>.Nth(</c>, <c>text=</c> o <c>HasText</c> es una línea que falta y pone el test en
/// rojo; migrar un E2E a un localizador estable también lo pone, para que la lista baje con el
/// trabajo. No migra ninguno de los E2E existentes.
/// </para>
/// </summary>
public class LocatorsDeE2ECongeladosTests
{
    [Fact]
    public void Los_localizadores_E2E_por_texto_o_posicion_solo_decrecen()
    {
        var fallo = ListaCongelada.Verificar("E2E-locators", AnalisisDeLocatorsE2E.MedirE2E(),
            "Un localizador por texto visible (GetByText, text=, :has-text) o por posición (.First, .Nth, :nth-…) " +
            "rompe el E2E cuando cambia un rótulo o se reordena una lista aunque el comportamiento sea el mismo " +
            "(clase C10 del análisis de causas raíz). Localiza por rol y nombre accesible estable, por etiqueta del " +
            "recurso, o —cuando el componente lo emita— por data-testid. Si es inevitable, añade la línea en el mismo " +
            "commit y justifícalo en la PR; si has MIGRADO localizadores, baja o borra su línea. " +
            "Símbolos: GetByText, Posicional, LocatorPorTexto, FiltroPorTexto.");

        fallo.Should().BeNull();
    }

    [Fact]
    public void El_escaner_ve_los_ficheros_de_E2E()
    {
        // Control positivo independiente de la lista: hay decenas de ficheros de E2E con localizadores.
        var medido = AnalisisDeLocatorsE2E.MedirE2E();

        medido.Keys.Select(u => u.Lugar).Distinct().Count().Should().BeGreaterThan(20);
        medido.Keys.Select(u => u.Simbolo).Should().Contain(AnalisisDeLocatorsE2E.SimboloGetByText);
    }

    // ───────────── control positivo del detector, con fuentes sintéticas ─────────────

    private static Dictionary<string, int> Analizar(string cuerpo) =>
        AnalisisDeLocatorsE2E.Analizar($"class T {{ async Task M(dynamic page) {{ {cuerpo} }} }}");

    [Theory]
    [InlineData("await page.GetByText(\"Guardar\").ClickAsync();", AnalisisDeLocatorsE2E.SimboloGetByText)]
    [InlineData("await page.GetByText(\"Guardar\", new() { Exact = true }).ClickAsync();", AnalisisDeLocatorsE2E.SimboloGetByText)]
    [InlineData("await page.Locator(\"tr\").First.ClickAsync();", AnalisisDeLocatorsE2E.SimboloPosicional)]
    [InlineData("await page.Locator(\"tr\").Last.ClickAsync();", AnalisisDeLocatorsE2E.SimboloPosicional)]
    [InlineData("await page.Locator(\"tr\").Nth(2).ClickAsync();", AnalisisDeLocatorsE2E.SimboloPosicional)]
    [InlineData("await page.Locator(\"tr:nth-child(2)\").ClickAsync();", AnalisisDeLocatorsE2E.SimboloPosicional)]
    [InlineData("await page.Locator(\"li:first-child\").ClickAsync();", AnalisisDeLocatorsE2E.SimboloPosicional)]
    [InlineData("await page.Locator(\"li >> nth=1\").ClickAsync();", AnalisisDeLocatorsE2E.SimboloPosicional)]
    [InlineData("await page.Locator(\"button:has-text('Guardar')\").ClickAsync();", AnalisisDeLocatorsE2E.SimboloPorTexto)]
    [InlineData("await page.Locator(\"text=Guardar\").ClickAsync();", AnalisisDeLocatorsE2E.SimboloPorTexto)]
    [InlineData("await page.Locator($\"text={nombre}\").ClickAsync();", AnalisisDeLocatorsE2E.SimboloPorTexto)]
    [InlineData("await page.ClickAsync(\"text=Guardar\");", AnalisisDeLocatorsE2E.SimboloPorTexto)]
    [InlineData("await page.Locator(\"tr:has-text('\" + nombre + \"')\").ClickAsync();", AnalisisDeLocatorsE2E.SimboloPorTexto)]
    [InlineData("await page.Locator(cond ? \"text=A\" : \"b\").ClickAsync();", AnalisisDeLocatorsE2E.SimboloPorTexto)]
    [InlineData("await page.Locator(\"xpath=//button[normalize-space()='Guardar']\").ClickAsync();", AnalisisDeLocatorsE2E.SimboloPorTexto)]
    [InlineData("await page.Locator(\"xpath=//button[text()='Guardar']\").ClickAsync();", AnalisisDeLocatorsE2E.SimboloPorTexto)]
    [InlineData("await page.Locator(\"tr\").Filter(new() { HasText = \"Fila\" }).ClickAsync();", AnalisisDeLocatorsE2E.SimboloFiltroPorTexto)]
    [InlineData("await page.Locator(\"tr\", new() { HasNotText = \"Fila\" }).ClickAsync();", AnalisisDeLocatorsE2E.SimboloFiltroPorTexto)]
    public void Cada_forma_fragil_de_localizar_se_detecta(string cuerpo, string simbolo) =>
        Analizar(cuerpo).Should().Equal(new Dictionary<string, int> { [simbolo] = 1 });

    [Theory]
    [InlineData("var x = lista.First();")]
    [InlineData("var x = lista.First(e => e.Ok);")]
    [InlineData("var x = lista.Last();")]
    [InlineData("await page.GetByRole(AriaRole.Button, new() { Name = \"Guardar\" }).ClickAsync();")]
    [InlineData("await page.GetByLabel(\"Nombre\").FillAsync(\"x\");")]
    [InlineData("await page.GetByTestId(\"guardar\").ClickAsync();")]
    [InlineData("await page.Locator(\"[data-testid='x']\").ClickAsync();")]
    [InlineData("await page.Locator(\"[data-text=1]\").ClickAsync();")]
    [InlineData("// await page.GetByText(\"Guardar\").First.ClickAsync();")]
    [InlineData("var s = \"GetByText y .First en una cadena\";")]
    [InlineData("var url = \"/buscar?text=hola\";")]
    [InlineData("const string Selector = \"li:first-child\";")]
    [InlineData("string s; s = \"button:has-text('x')\";")]
    public void Lo_que_no_es_fragil_no_cuenta(string cuerpo) =>
        Analizar(cuerpo).Should().BeEmpty();

    [Fact]
    public void Se_acumula_por_simbolo_dentro_del_fichero()
    {
        var cuenta = Analizar("""
            await page.GetByText("a").First.ClickAsync();
            await page.GetByText("b").ClickAsync();
            await page.Locator("tr").Nth(1).ClickAsync();
            """);

        cuenta.Should().Equal(new Dictionary<string, int>
        {
            [AnalisisDeLocatorsE2E.SimboloGetByText] = 2,
            [AnalisisDeLocatorsE2E.SimboloPosicional] = 2,
        });
    }
}
