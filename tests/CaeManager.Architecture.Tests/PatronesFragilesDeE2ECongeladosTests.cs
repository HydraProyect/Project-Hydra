using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Los patrones de E2E que más veces expulsaron una PR de la cola de fusión, congelados por fichero (2026-10-18). En los
/// últimos 7 días, 21 de 30 expulsiones fueron de E2E: #1340 y #1345 esperaron la clase <c>fila-enfocada</c> sin foco
/// real del DOM y pulsaron Enter (mismo arreglo dos veces), #1413 hizo clic en un enlace de un grupo del menú plegado.
/// Los ayudantes que lo evitan viven en <c>Ayudas.cs</c> (<c>EsperarFocoRealEnFilaAsync</c>, <c>EnfocarFilaAsync</c>,
/// <c>EsperarListaListaAsync</c>, <c>AbrirGrupoYClickAsync</c>); esta guarda impide que el patrón vuelva a entrar.
///
/// <para>
/// <b>Lista por ubicación</b> (<c>Congelados/E2E-patrones-fragiles.txt</c>, <see cref="ListaCongelada"/>): una línea por
/// <c>fichero :: patrón = n</c>. Un patrón nuevo es una línea que falta (<c>NUEVA</c>) o una cifra que crece
/// (<c>CRECE</c>); migrar uno al ayudante lo baja (<c>BAJA</c>, <c>OBSOLETA</c>), para que la lista solo decrezca.
/// </para>
///
/// <para>
/// <b>Contrato efectivo, más estrecho que el nombre</b>: lo que el detector (<see cref="AnalisisDePatronesFragilesE2E"/>)
/// no ve está escrito en su documentación (localizador recibido como parámetro, tecla pulsada en otro método, grupos
/// plegados que no estén en su lista). No mide carreras de otro tipo (p. ej. un <c>CountAsync()</c> instantáneo) ni
/// sustituye a ejecutar el job E2E.
/// </para>
/// </summary>
public class PatronesFragilesDeE2ECongeladosTests
{
    private const string Lista = "E2E-patrones-fragiles";

    private const string Guia =
        "NUEVA o CRECE: no añadas ni subas la línea. FocoSoloPorClase: tras pulsar «j»/«k», espera el foco real de la fila con " +
        "Ayudas.EsperarFocoRealEnFilaAsync(fila) (o Ayudas.EnfocarFilaAsync) antes de pulsar otra tecla: la clase fila-enfocada " +
        "llega con el parche del servidor y el foco del DOM un fotograma después, y con el foco aún en el control de origen Enter " +
        "se descarta (#1340, #1345). ClicEnGrupoPlegado: un enlace de «Administración» o «Plataforma» no es visible hasta abrir su " +
        "grupo; usa Ayudas.AbrirGrupoYClickAsync(page, idGrupo, enlace) (#1413). BAJA u OBSOLETA: has migrado patrones; baja la " +
        "cifra o borra la línea en tests/CaeManager.Architecture.Tests/Congelados/" + Lista + ".txt " +
        "(HYDRA_TRINQUETES_VOLCAR=<directorio> vuelca la medida).";

    [Fact]
    public void Los_patrones_fragiles_de_E2E_solo_decrecen()
    {
        var fuentes = AnalisisDeSuiteE2E.LeerE2E();

        var fallo = ListaCongelada.Verificar(Lista, AnalisisDePatronesFragilesE2E.Medir(fuentes), Guia + DondeEstan(fuentes, LeerLista()));

        fallo.Should().BeNull();
    }

    [Fact]
    public void El_escaner_ve_los_ficheros_de_E2E()
    {
        // Control positivo independiente de la lista: si el recorrido no leyera la suite, «ningún patrón nuevo» valdría por vacío.
        var fuentes = AnalisisDeSuiteE2E.LeerE2E();

        fuentes.Should().HaveCountGreaterThan(50);
        fuentes.Select(f => f.Texto).Should().Contain(t => t.Contains("AbrirGrupoDelMenuAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void Los_ayudantes_que_evitan_los_patrones_existen_en_Ayudas()
    {
        // Si alguien renombra un ayudante, la guía de arriba y los E2E migrados quedarían apuntando a nada.
        var ayudas = AnalisisDeSuiteE2E.LeerE2E().Single(f => f.Ruta == "tests/CaeManager.E2ETests/Ayudas.cs").Texto;

        ayudas.Should().Contain("Task EsperarFocoRealEnFilaAsync(").And.Contain("Task EnfocarFilaAsync(")
            .And.Contain("Task EsperarListaListaAsync(").And.Contain("Task AbrirGrupoYClickAsync(");
    }

    // ───────────── control positivo del detector, con fuentes sintéticas ─────────────

    private static List<PatronFragil> Analizar(string cuerpo) =>
        AnalisisDePatronesFragilesE2E.Analizar($"class T {{ async Task M(dynamic page) {{ {cuerpo} }} }}");

    [Theory]
    // Clase y tecla sin foco real: el fallo de #1340 y #1345.
    [InlineData("await Expect(page.Locator(\"tbody tr.fila-enfocada\")).ToHaveCountAsync(1); await page.Keyboard.PressAsync(\"Enter\");")]
    [InlineData("await Assertions.Expect(page.Locator(\".fila-enfocada\")).ToHaveCountAsync(1, new() { Timeout = 5 }); await page.Keyboard.PressAsync(\"e\");")]
    [InlineData("var enfocada = page.Locator(\"tbody tr.fila-enfocada\"); await Expect(enfocada).ToHaveCountAsync(1); await page.Keyboard.PressAsync(\"e\");")]
    [InlineData("await Expect(fila).ToHaveClassAsync(new Regex(\"fila-enfocada\")); await page.Keyboard.PressAsync(\"Enter\");")]
    [InlineData("await Expect(fila).ToHaveClassAsync(new Regex(@\"\\bfila-enfocada\\b\")); await fila.PressAsync(\"Enter\");")]
    // Una tecla pulsada antes de confirmar el foco sigue siendo frágil aunque el foco se espere después.
    [InlineData("await Expect(page.Locator(\".fila-enfocada\")).ToHaveCountAsync(1); await page.Keyboard.PressAsync(\"e\"); await Expect(page.Locator(\".fila-enfocada\")).ToBeFocusedAsync();")]
    public void Esperar_solo_la_clase_de_foco_y_pulsar_una_tecla_se_detecta(string cuerpo) =>
        Analizar(cuerpo).Should().ContainSingle().Which.Simbolo.Should().Be(AnalisisDePatronesFragilesE2E.FocoSoloPorClase);

    [Fact]
    public void Un_ayudante_Enfocar_que_termina_en_la_espera_por_clase_se_detecta()
    {
        var patrones = AnalisisDePatronesFragilesE2E.Analizar(
            "class T { static async Task EnfocarLaFilaAsync(dynamic page) { await page.Keyboard.PressAsync(\"j\");\nawait Expect(page.Locator(\"tr.fila-enfocada\")).ToHaveCountAsync(1);\n} }");

        patrones.Should().Equal(new PatronFragil(AnalisisDePatronesFragilesE2E.FocoSoloPorClase, 2));
    }

    [Theory]
    [InlineData("await Expect(page.Locator(\"tbody tr.fila-enfocada\")).ToHaveCountAsync(1); await Expect(page.Locator(\"tbody tr.fila-enfocada\")).ToBeFocusedAsync(); await page.Keyboard.PressAsync(\"Enter\");")]
    [InlineData("await Expect(fila).ToHaveClassAsync(new Regex(\"fila-enfocada\")); await Expect(fila).ToBeFocusedAsync();")]
    // El foco real confirmado justo antes de la clase protege igual (GestionesFase1TecladoTests lo escribe en ese orden).
    [InlineData("await Expect(fila).ToBeFocusedAsync(); await Expect(fila).ToHaveClassAsync(new Regex(\"fila-enfocada\")); await page.Keyboard.PressAsync(\"k\");")]
    [InlineData("await Ayudas.EsperarFocoRealEnFilaAsync(page.Locator(\"tbody tr.fila-enfocada\")); await page.Keyboard.PressAsync(\"Enter\");")]
    [InlineData("await Ayudas.EnfocarFilaAsync(page, cabecera, page.Locator(\"tbody tr.fila-enfocada\")); await page.Keyboard.PressAsync(\"Enter\");")]
    // Esperar la clase sin pulsar nada después no depende del foco (comprobar que una fila conserva la clase tras refrescar).
    [InlineData("await Expect(page.Locator(\"tbody tr.fila-enfocada\")).ToHaveCountAsync(1); await Expect(titulo).ToHaveTextAsync(\"x\");")]
    // Que no haya fila enfocada, o la espera de otra clase, no es esperar el foco.
    [InlineData("await Expect(page.Locator(\".fila-enfocada\")).ToHaveCountAsync(0); await page.Keyboard.PressAsync(\"j\");")]
    [InlineData("await Expect(page.Locator(\".fila-enfocada\")).Not.ToHaveCountAsync(1); await page.Keyboard.PressAsync(\"j\");")]
    [InlineData("await Expect(fila).ToHaveClassAsync(new Regex(\"bandeja-fila-activa\")); await page.Keyboard.PressAsync(\"Enter\");")]
    [InlineData("await Expect(page.Locator(\"tbody tr\")).ToHaveCountAsync(1); await page.Keyboard.PressAsync(\"Enter\");")]
    [InlineData("// await Expect(page.Locator(\".fila-enfocada\")).ToHaveCountAsync(1); await page.Keyboard.PressAsync(\"Enter\");")]
    [InlineData("var s = \"Expect(x.fila-enfocada).ToHaveCountAsync(1)\"; await page.Keyboard.PressAsync(\"Enter\");")]
    public void Lo_que_confirma_el_foco_real_o_no_depende_de_el_no_cuenta(string cuerpo) =>
        Analizar(cuerpo).Should().BeEmpty();

    [Theory]
    [InlineData("await page.Locator(\"a.nav-item[href='delegaciones']\").ClickAsync();")]
    [InlineData("await page.Locator(\"nav a.nav-item[href='usuarios']\").First.ClickAsync();")]
    [InlineData("await page.Locator(\".nav-principal a.nav-item[href='configuracion/comercial']\").ClickAsync();")]
    [InlineData("await page.Locator(\"a.nav-item[href='plataforma/conectores-cae']\").ClickAsync();")]
    [InlineData("await page.Locator(\".nav-item\", new PageLocatorOptions { HasText = \"Delegaciones\" }).ClickAsync();")]
    [InlineData("await page.Locator($\"a.nav-item[href='auditoria']\").ClickAsync();")]
    [InlineData("await page.Locator(\"a.nav-item\").Filter(new() { HasText = \"Configuración\" }).ClickAsync();")]
    public void Un_clic_en_un_enlace_de_un_grupo_plegado_sin_abrirlo_se_detecta(string cuerpo) =>
        Analizar(cuerpo).Should().ContainSingle().Which.Simbolo.Should().Be(AnalisisDePatronesFragilesE2E.ClicEnGrupoPlegado);

    [Theory]
    [InlineData("await Ayudas.AbrirGrupoDelMenuAsync(page, \"plataforma\"); await page.Locator(\"a.nav-item[href='delegaciones']\").ClickAsync();")]
    [InlineData("await Ayudas.AbrirGrupoYClickAsync(page, \"plataforma\", page.Locator(\"a.nav-item[href='delegaciones']\"));")]
    // Enlaces de grupos abiertos por defecto, y rótulos fuera del menú.
    [InlineData("await page.Locator(\"a.nav-item[href='empresas']\").First.ClickAsync();")]
    [InlineData("await page.Locator(\"a.nav-item[href='mi-trabajo']\").ClickAsync();")]
    [InlineData("await page.Locator(\"nav a.nav-item\", new() { HasText = \"Mi trabajo\" }).ClickAsync();")]
    [InlineData("await page.Locator(\"a.barra-seccion-enlace[href='configuracion']\").ClickAsync();")]
    [InlineData("await page.GetByRole(AriaRole.Link, new() { Name = \"Delegaciones\" }).ClickAsync();")]
    [InlineData("// await page.Locator(\"a.nav-item[href='delegaciones']\").ClickAsync();")]
    [InlineData("var s = \"a.nav-item[href='delegaciones']\";")]
    public void Abrir_el_grupo_antes_o_un_enlace_de_grupo_abierto_no_cuenta(string cuerpo) =>
        Analizar(cuerpo).Should().BeEmpty();

    [Fact]
    public void Cada_patron_lleva_su_linea_y_se_acumula_por_simbolo_dentro_del_fichero()
    {
        const string fuente = """
            class T
            {
                async Task M(dynamic page)
                {
                    await page.Locator("a.nav-item[href='usuarios']").ClickAsync();
                    await Expect(page.Locator(".fila-enfocada")).ToHaveCountAsync(1);
                    await page.Keyboard.PressAsync("Enter");
                    await page.Locator("a.nav-item[href='auditoria']").ClickAsync();
                }
            }
            """;

        AnalisisDePatronesFragilesE2E.Analizar(fuente).Should().Equal(
            new PatronFragil(AnalisisDePatronesFragilesE2E.ClicEnGrupoPlegado, 5),
            new PatronFragil(AnalisisDePatronesFragilesE2E.FocoSoloPorClase, 6),
            new PatronFragil(AnalisisDePatronesFragilesE2E.ClicEnGrupoPlegado, 8));

        var medido = AnalisisDePatronesFragilesE2E.Medir([("a/Uno.cs", fuente), ("a/Dos.cs", "class U { }")]);

        medido.Should().HaveCount(2, "un fichero sin patrones no entra en la medida");
        medido[new Ubicacion("a/Uno.cs", AnalisisDePatronesFragilesE2E.ClicEnGrupoPlegado)].Should().Be(2);
        medido[new Ubicacion("a/Uno.cs", AnalisisDePatronesFragilesE2E.FocoSoloPorClase)].Should().Be(1);
    }

    [Fact]
    public void El_mensaje_dice_fichero_y_linea_solo_de_lo_que_crece_o_es_nuevo()
    {
        const string dos = "class T { async Task M(dynamic page) {\nawait page.Locator(\"a.nav-item[href='usuarios']\").ClickAsync();\nawait page.Locator(\"a.nav-item[href='auditoria']\").ClickAsync();\n} }";
        List<(string Ruta, string Texto)> fuentes = [("a/Crece.cs", dos), ("a/Nueva.cs", dos), ("a/Igual.cs", dos)];
        var listado = new Dictionary<Ubicacion, int>
        {
            [new Ubicacion("a/Crece.cs", AnalisisDePatronesFragilesE2E.ClicEnGrupoPlegado)] = 1,
            [new Ubicacion("a/Igual.cs", AnalisisDePatronesFragilesE2E.ClicEnGrupoPlegado)] = 2,
        };

        var donde = DondeEstan(fuentes, listado);

        donde.Should().Contain("a/Crece.cs:2").And.Contain("a/Crece.cs:3").And.Contain("a/Nueva.cs:2");
        donde.Should().NotContain("a/Igual.cs");
        DondeEstan([("a/Igual.cs", dos)], listado).Should().BeEmpty("sin nada que crezca no hay nada que señalar");
    }

    private static IReadOnlyDictionary<Ubicacion, int> LeerLista()
    {
        var ruta = ListaCongelada.RutaDeLista(Lista);
        return File.Exists(ruta) ? ListaCongelada.Leer(File.ReadAllText(ruta)) : new Dictionary<Ubicacion, int>();
    }

    /// <summary>Fichero y línea de cada patrón de las ubicaciones que superan su cifra o no están listadas.</summary>
    private static string DondeEstan(IEnumerable<(string Ruta, string Texto)> fuentes, IReadOnlyDictionary<Ubicacion, int> listado)
    {
        var lineas = new List<string>();

        foreach (var (ruta, texto) in fuentes)
        {
            foreach (var porSimbolo in AnalisisDePatronesFragilesE2E.Analizar(texto).GroupBy(p => p.Simbolo))
            {
                if (porSimbolo.Count() > listado.GetValueOrDefault(new Ubicacion(ruta, porSimbolo.Key)))
                    lineas.AddRange(porSimbolo.Select(p => $"{ruta}:{p.Linea}  {p.Simbolo}"));
            }
        }

        return lineas.Count == 0 ? string.Empty : "\nPatrones de las ubicaciones NUEVA o CRECE (fichero:línea):\n  " + string.Join("\n  ", lineas);
    }
}
