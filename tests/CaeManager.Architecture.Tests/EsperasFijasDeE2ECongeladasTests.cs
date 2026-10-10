using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Las esperas fijas de la suite E2E, congeladas por fichero (2026-10-10). El job E2E de CI pasó de 8,5 a 24 minutos en
/// cinco semanas, y una de las causas que se controlan con trinquete es esta: <c>page.WaitForTimeoutAsync(n)</c> y
/// <c>Task.Delay(n)</c> son tiempo muerto que se paga en cada run, y además esconden la condición que de verdad se
/// espera (cuando la máquina va lenta, la espera se queda corta y el test falla una vez sí y otra no).
///
/// <para>
/// <b>Lista por ubicación</b> (<c>Congelados/E2E-esperas-fijas.txt</c>, <see cref="ListaCongelada"/>): una línea por
/// <c>fichero :: llamada = n</c>. Una espera nueva en un fichero sin ninguna es una línea que falta (<c>NUEVA</c>); una
/// más en un fichero listado, <c>CRECE</c>; las dos ponen el test en rojo con el fichero y la línea de cada espera.
/// Retirar una también lo pone (<c>BAJA</c>, <c>OBSOLETA</c>), con otro mensaje: bajar la cifra, para que la lista no se
/// vuelva permisiva y el hueco que deja una espera retirada no lo ocupe otra.
/// </para>
///
/// <para>
/// <b>Contrato efectivo, más estrecho que el nombre.</b> El detector es sintáctico
/// (<see cref="AnalisisDeSuiteE2E.EsperasFijas"/>): cuenta invocaciones <c>….WaitForTimeoutAsync(…)</c>,
/// <c>Task.Delay(…)</c> y <c>Thread.Sleep(…)</c>. NO ve: una espera envuelta en un ayudante (el <c>Task.Delay</c> cuenta una
/// vez, en el ayudante, por muchas veces que se le llame; los bucles de sondeo de <c>Ayudas.cs</c> son eso mismo);
/// <c>Delay(…)</c> a secas tras un <c>using static</c> de <c>Task</c>; un alias de tipo; ni un tiempo de espera fijo pasado
/// por opciones (<c>SlowMo</c>, un <c>Timeout</c> generoso que siempre se agota). Tampoco mide la duración: una espera de
/// 50 ms y una de 6 s cuentan igual.
/// </para>
/// </summary>
public class EsperasFijasDeE2ECongeladasTests
{
    private const string Lista = "E2E-esperas-fijas";

    private const string Guia =
        "Una espera fija en un E2E es tiempo muerto en cada run del job y esconde la condición que de verdad se espera. " +
        "NUEVA o CRECE: no añadas ni subas la línea; espera la condición con una aserción de Playwright, que reintenta sola " +
        "(await Expect(locator).ToBeVisibleAsync(), ToHaveTextAsync, ToHaveCountAsync…), con page.WaitForFunctionAsync(…) o con " +
        "page.WaitForURLAsync(…). Solo si la espera ES un reloj del servidor (un ciclo de revalidación, una caducidad) se " +
        "añade o se sube la línea en tests/CaeManager.Architecture.Tests/Congelados/" + Lista + ".txt, con un comentario '#' " +
        "encima que diga qué reloj es. BAJA u OBSOLETA: has retirado esperas; baja la cifra o borra la línea en ese mismo " +
        "fichero, para que la lista no quede permisiva (HYDRA_TRINQUETES_VOLCAR=<directorio> vuelca la medida).";

    [Fact]
    public void Las_esperas_fijas_de_E2E_solo_decrecen()
    {
        var fuentes = AnalisisDeSuiteE2E.LeerE2E();

        var fallo = ListaCongelada.Verificar(Lista, AnalisisDeSuiteE2E.MedirEsperasFijas(fuentes), Guia + DondeEstan(fuentes, LeerLista()));

        fallo.Should().BeNull();
    }

    [Fact]
    public void El_escaner_ve_los_ficheros_de_E2E()
    {
        // Control positivo independiente de la lista: si el recorrido no leyera la suite, «ninguna espera nueva» valdría por vacío.
        var fuentes = AnalisisDeSuiteE2E.LeerE2E();
        var medido = AnalisisDeSuiteE2E.MedirEsperasFijas(fuentes);

        fuentes.Should().HaveCountGreaterThan(50, "había 86 ficheros .cs en tests/CaeManager.E2ETests al escribirlo; si baja de golpe, dejó de mirar");
        fuentes.Select(f => f.Ruta).Should().OnlyContain(r => r.StartsWith("tests/CaeManager.E2ETests/", StringComparison.Ordinal) && !r.Contains('\\'));
        medido.Keys.Select(u => u.Simbolo).Should().Contain([AnalisisDeSuiteE2E.EsperaDePlaywright, AnalisisDeSuiteE2E.EsperaDeTarea]);
    }

    // ───────────── control positivo del detector, con fuentes sintéticas y nunca con una entrada de la lista ─────────────

    private static List<EsperaFija> Esperas(string cuerpo) =>
        AnalisisDeSuiteE2E.EsperasFijas($"class T {{ async Task M(dynamic page, dynamic? pagina) {{ {cuerpo} }} }}");

    [Theory]
    [InlineData("await page.WaitForTimeoutAsync(50);", AnalisisDeSuiteE2E.EsperaDePlaywright)]
    [InlineData("await Page.WaitForTimeoutAsync(1_000);", AnalisisDeSuiteE2E.EsperaDePlaywright)]
    [InlineData("await contexto.Pages[0].WaitForTimeoutAsync(50);", AnalisisDeSuiteE2E.EsperaDePlaywright)]
    [InlineData("await (pagina?.WaitForTimeoutAsync(50) ?? Task.CompletedTask);", AnalisisDeSuiteE2E.EsperaDePlaywright)]
    [InlineData("await Task.Delay(100);", AnalisisDeSuiteE2E.EsperaDeTarea)]
    [InlineData("await Task.Delay(TimeSpan.FromSeconds(3));", AnalisisDeSuiteE2E.EsperaDeTarea)]
    [InlineData("await System.Threading.Tasks.Task.Delay(100);", AnalisisDeSuiteE2E.EsperaDeTarea)]
    [InlineData("await global::System.Threading.Tasks.Task.Delay(100);", AnalisisDeSuiteE2E.EsperaDeTarea)]
    [InlineData("var espera = Task.Delay(100); await espera;", AnalisisDeSuiteE2E.EsperaDeTarea)]
    [InlineData("Thread.Sleep(100);", AnalisisDeSuiteE2E.EsperaDeHilo)]
    [InlineData("System.Threading.Thread.Sleep(100);", AnalisisDeSuiteE2E.EsperaDeHilo)]
    public void Una_espera_fija_en_codigo_cuenta(string cuerpo, string simbolo)
    {
        Esperas(cuerpo).Should().ContainSingle().Which.Simbolo.Should().Be(simbolo);
    }

    [Theory]
    [InlineData("// await page.WaitForTimeoutAsync(50);")]
    [InlineData("/* await Task.Delay(100); */")]
    [InlineData("/// <c>page.RouteAsync(…Task.Delay(6000)…)</c>\n")]
    [InlineData("var s = \"await page.WaitForTimeoutAsync(50);\";")]
    [InlineData("var s = \"Task.Delay(100)\";")]
    [InlineData("var s = @\"Thread.Sleep(100)\";")]
    [InlineData("var s = \"\"\"await Task.Delay(100);\"\"\";")]
    [InlineData("var s = $\"{page} Task.Delay(100)\";")]
    [InlineData("await page.WaitForFunctionAsync(\"() => true\");")]
    [InlineData("await page.WaitForURLAsync(\"**/centros\");")]
    [InlineData("await Task.WhenAll(a, b);")]
    [InlineData("await reloj.Delay(100);")]
    [InlineData("var retraso = opciones.Delay;")]
    [InlineData("hilo.Sleep(100);")]
    public void Un_comentario_una_cadena_o_una_llamada_parecida_no_cuentan(string cuerpo)
    {
        Esperas(cuerpo).Should().BeEmpty();
    }

    [Fact]
    public void Cada_espera_lleva_su_linea_y_un_texto_con_varias_las_cuenta_todas()
    {
        const string fuente = """
            class T
            {
                async Task M(dynamic page)
                {
                    await page.WaitForTimeoutAsync(50);
                    // await page.WaitForTimeoutAsync(50);
                    await Task.Delay(100);
                    await page.WaitForTimeoutAsync(50);
                }
            }
            """;

        AnalisisDeSuiteE2E.EsperasFijas(fuente).Should().Equal(
            new EsperaFija(AnalisisDeSuiteE2E.EsperaDePlaywright, 5),
            new EsperaFija(AnalisisDeSuiteE2E.EsperaDeTarea, 7),
            new EsperaFija(AnalisisDeSuiteE2E.EsperaDePlaywright, 8));

        var medido = AnalisisDeSuiteE2E.MedirEsperasFijas([("a/Uno.cs", fuente), ("a/Dos.cs", "class U { }"), ("a/Tres.cs", fuente)]);

        medido.Should().HaveCount(4, "un fichero sin esperas no entra en la medida, y cada llamada tiene su línea");
        medido[new Ubicacion("a/Uno.cs", AnalisisDeSuiteE2E.EsperaDePlaywright)].Should().Be(2);
        medido[new Ubicacion("a/Tres.cs", AnalisisDeSuiteE2E.EsperaDeTarea)].Should().Be(1);
    }

    [Fact]
    public void El_mensaje_dice_fichero_y_linea_solo_de_lo_que_crece_o_es_nuevo()
    {
        const string dos = "class T { async Task M(dynamic page) {\nawait page.WaitForTimeoutAsync(1);\nawait page.WaitForTimeoutAsync(2);\n} }";
        List<(string Ruta, string Texto)> fuentes = [("a/Crece.cs", dos), ("a/Nueva.cs", dos), ("a/Igual.cs", dos), ("a/Baja.cs", dos)];
        var listado = new Dictionary<Ubicacion, int>
        {
            [new Ubicacion("a/Crece.cs", AnalisisDeSuiteE2E.EsperaDePlaywright)] = 1,
            [new Ubicacion("a/Igual.cs", AnalisisDeSuiteE2E.EsperaDePlaywright)] = 2,
            [new Ubicacion("a/Baja.cs", AnalisisDeSuiteE2E.EsperaDePlaywright)] = 3,
        };

        var donde = DondeEstan(fuentes, listado);

        donde.Should().Contain("a/Crece.cs:2").And.Contain("a/Crece.cs:3").And.Contain("a/Nueva.cs:2");
        donde.Should().NotContain("a/Igual.cs").And.NotContain("a/Baja.cs");
        DondeEstan([("a/Igual.cs", dos)], listado).Should().BeEmpty("sin nada que crezca no hay nada que señalar");
    }

    private static IReadOnlyDictionary<Ubicacion, int> LeerLista()
    {
        var ruta = ListaCongelada.RutaDeLista(Lista);
        return File.Exists(ruta) ? ListaCongelada.Leer(File.ReadAllText(ruta)) : new Dictionary<Ubicacion, int>();
    }

    /// <summary>
    /// <see cref="ListaCongelada"/> dice qué ubicación se desvía y cuánto; esto añade <b>dónde</b>: fichero y línea de cada
    /// espera de las ubicaciones que superan su cifra o no están listadas (en un fichero con veinte, no se sabe cuál es la
    /// nueva, así que se enseñan todas las suyas).
    /// </summary>
    private static string DondeEstan(IEnumerable<(string Ruta, string Texto)> fuentes, IReadOnlyDictionary<Ubicacion, int> listado)
    {
        var lineas = new List<string>();

        foreach (var (ruta, texto) in fuentes)
        {
            foreach (var porSimbolo in AnalisisDeSuiteE2E.EsperasFijas(texto).GroupBy(e => e.Simbolo))
            {
                if (porSimbolo.Count() > listado.GetValueOrDefault(new Ubicacion(ruta, porSimbolo.Key)))
                    lineas.AddRange(porSimbolo.Select(e => $"{ruta}:{e.Linea}  {e.Simbolo}"));
            }
        }

        return lineas.Count == 0 ? string.Empty : "\nEsperas de las ubicaciones NUEVA o CRECE (fichero:línea):\n  " + string.Join("\n  ", lineas);
    }
}
