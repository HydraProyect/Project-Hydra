using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Invariante del shell (decisión de Chris del 2026-11-08, «Constrúyelas antes», punto 1): el que desplaza es
/// <c>main.contenido</c>, no el documento. Con <c>.shell { min-height: 100vh }</c>, <c>.contenido</c> crecía hasta medir todo su
/// contenido: era un contenedor de scroll (<c>overflow-y: auto</c>) que nunca desplazaba, y ningún <c>position: sticky</c> de
/// dentro se pegaba (la cabecera de las tablas, la columna lateral de las fichas, el pie fijo de la pantalla de pasos).
/// La altura real la mide el navegador (bUnit no calcula maquetación); aquí se fija la cadena que la acota —el shell mide la
/// ventana, la columna flex y <c>.contenido</c> pueden encoger con <c>min-height: 0</c>— y que <c>cabecera.js</c> sigue al
/// contenedor que desplaza y no a la ventana.
/// </summary>
public class ShellDesplazaContenidoTests
{
    private sealed record Regla(string? Media, string Selector, string Cuerpo);

    private static string Raiz()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio");
        return dir!;
    }

    private static string Leer(params string[] partes) =>
        File.ReadAllText(Path.Combine(new[] { Raiz(), "src", "CaeManager.Web" }.Concat(partes).ToArray()));

    private static List<Regla> Analizar(string css)
    {
        css = Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var reglas = new List<Regla>();
        var i = 0;
        AnalizarBloque(css, ref i, null, reglas);
        return reglas;
    }

    private static void AnalizarBloque(string css, ref int i, string? media, List<Regla> reglas)
    {
        while (i < css.Length)
        {
            var apertura = css.IndexOf('{', i);
            var cierre = css.IndexOf('}', i);
            if (cierre >= 0 && (apertura < 0 || cierre < apertura))
            {
                i = cierre + 1;
                return;
            }
            if (apertura < 0) { i = css.Length; return; }

            var preludio = Regex.Replace(css[i..apertura].Trim(), @"\s+", " ");
            i = apertura + 1;
            if (preludio.StartsWith('@'))
            {
                AnalizarBloque(css, ref i, preludio, reglas);
            }
            else
            {
                var fin = css.IndexOf('}', i);
                if (fin < 0) fin = css.Length;
                reglas.Add(new Regla(media, preludio, css[i..fin]));
                i = Math.Min(fin + 1, css.Length);
            }
        }
    }

    /// <summary>Declaraciones de un selector exacto en reglas sin @media, en orden (la última gana).</summary>
    private static List<(string Propiedad, string Valor)> DeclaracionesSinMedia(string css, string selector) =>
        Analizar(css)
            .Where(r => r.Media is null && r.Selector.Split(',').Any(s => s.Trim() == selector))
            .SelectMany(r => r.Cuerpo.Split(';'))
            .Select(d => d.Split(':', 2))
            .Where(p => p.Length == 2)
            .Select(p => (p[0].Trim(), Regex.Replace(p[1].Trim(), @"\s+", " ")))
            .ToList();

    private static string? Valor(List<(string Propiedad, string Valor)> declaraciones, string propiedad) =>
        declaraciones.LastOrDefault(d => d.Propiedad == propiedad).Valor;

    /// <summary>Lo que hace falta para que <c>.contenido</c> desplace: los hallazgos vacíos significan que la cadena está acotada.</summary>
    private static List<string> DefectosDeLaCadena(string css)
    {
        var defectos = new List<string>();
        var shell = DeclaracionesSinMedia(css, ".shell");
        if (Valor(shell, "height") is not ("100dvh" or "100vh"))
            defectos.Add(".shell no acota su altura a la ventana (height: 100dvh)");
        if (Valor(shell, "min-height") is { } minShell && minShell != "0")
            defectos.Add($".shell vuelve a crecer con su contenido (min-height: {minShell})");

        if (Valor(DeclaracionesSinMedia(css, ".area-principal"), "min-height") != "0")
            defectos.Add(".area-principal no deja encoger la columna (min-height: 0)");

        var contenido = DeclaracionesSinMedia(css, ".contenido");
        if (Valor(contenido, "min-height") != "0")
            defectos.Add(".contenido no puede encoger por debajo de su contenido (min-height: 0)");
        var overflow = Valor(contenido, "overflow-y") ?? Valor(contenido, "overflow");
        if (overflow is not ("auto" or "scroll"))
            defectos.Add($".contenido no es el contenedor que desplaza (overflow-y: {overflow ?? "sin declarar"})");

        // Ningún @media (salvo print) puede tocar la altura ni el overflow de la cadena: un «arreglo para móvil» con
        // min-height: 100vh devolvería el desplazamiento al documento solo en ese ancho, y aquí nadie lo vería.
        var eslabones = new[] { ".shell", ".area-principal", ".contenido" };
        foreach (var r in Analizar(css).Where(r => r.Media is not null && r.Media != "@media print"))
        {
            if (!r.Selector.Split(',').Any(s => eslabones.Contains(s.Trim()))) continue;
            if (Regex.IsMatch(r.Cuerpo, @"(?<![-\w])(min-height|max-height|height|overflow|overflow-y)\s*:"))
                defectos.Add($"{r.Media} vuelve a tocar la altura o el overflow de «{r.Selector}»");
        }
        return defectos;
    }

    [Fact]
    public void El_shell_mide_la_ventana_y_el_que_desplaza_es_main_contenido()
    {
        DefectosDeLaCadena(Leer("Components", "Layout", "MainLayout.razor.css")).Should().BeEmpty(
            "si .contenido crece hasta su contenido desplaza el documento y ningún position:sticky de una pantalla se pega");
    }

    [Fact]
    public void Al_imprimir_el_shell_suelta_la_altura_para_que_salga_la_pagina_entera()
    {
        var print = Analizar(Leer("Components", "Layout", "MainLayout.razor.css")).Where(r => r.Media == "@media print").ToList();

        print.Should().Contain(r => r.Selector.Split(',').Any(s => s.Trim() == ".shell") && Regex.IsMatch(r.Cuerpo, @"(?<![-\w])height\s*:\s*auto"),
            "con la altura acotada solo se imprimiría lo visible de .contenido");
        print.Should().Contain(r => r.Selector == ".contenido" && Regex.IsMatch(r.Cuerpo, @"overflow\s*:\s*visible"));
    }

    [Fact]
    public void La_cabecera_sigue_el_desplazamiento_de_main_contenido_y_lo_devuelve_arriba_al_cambiar_de_pantalla()
    {
        var js = Regex.Replace(Leer("wwwroot", "js", "cabecera.js"), @"//.*", string.Empty);

        js.Should().Contain("getElementById('contenido-principal')", "la sombra de la cabecera se calcula sobre el contenedor que desplaza");
        js.Should().MatchRegex(@"contenido\.scrollTop\s*>|\.scrollTop\s*:\s*0\)\s*>", "lee el desplazamiento de .contenido");
        js.Should().NotContain("window.scrollY", "la ventana ya no desplaza: leerla dejaría la cabecera sin sombra");
        js.Should().MatchRegex(@"addEventListener\('scroll',[\s\S]*?capture:\s*true",
            "el scroll de un elemento no burbujea hasta document: se escucha en captura");
        js.Should().MatchRegex(@"addEventListener\('enhancedload',\s*volverArribaSiCambiaLaPantalla\)",
            "Blazor solo devuelve arriba la ventana; .contenido conservaría el scroll de la pantalla anterior");
        js.Should().MatchRegex(@"window\.addEventListener\('load',\s*engancharNavegacion\)",
            "si Blazor aún no expone addEventListener, el enganche se reintenta en load en vez de perderse en silencio");
        js.Should().MatchRegex(@"contenido\.scrollTo\(\{\s*top:\s*0,\s*behavior:\s*'instant'\s*\}\)",
            ".contenido lleva scroll-behavior: smooth (base.css): la vuelta arriba al cambiar de pantalla no se anima");
    }

    [Fact]
    public void El_teclado_de_la_cabecera_cumple_su_contrato_ejecutando_el_script()
    {
        // Las guardas del manejador de teclado (modal, foco, Ctrl/Alt/Meta, Mayús, defaultPrevented, clic en otro scroller,
        // caducidad del clic) se prueban por su efecto, no por el texto: js/cabeceraTeclado.test.mjs evalúa cabecera.js con un
        // document falso. Necesita Node (el mismo del job deploy-script-tests); sin él esta prueba falla en vez de saltarse.
        var prueba = Path.Combine(Raiz(), "tests", "CaeManager.Web.Tests", "js", "cabeceraTeclado.test.mjs");
        File.Exists(prueba).Should().BeTrue();
        var info = new System.Diagnostics.ProcessStartInfo("node", $"--test \"{prueba}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        System.Diagnostics.Process proceso;
        try { proceso = System.Diagnostics.Process.Start(info)!; }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("Hace falta Node en el PATH para ejecutar js/cabeceraTeclado.test.mjs: " + ex.Message, ex);
        }
        using (proceso)
        {
            var salida = proceso.StandardOutput.ReadToEndAsync();
            var error = proceso.StandardError.ReadToEndAsync();
            proceso.WaitForExit(TimeSpan.FromSeconds(60)).Should().BeTrue("node --test no debe colgarse");
            proceso.ExitCode.Should().Be(0, "las pruebas de Node fallaron:\n" + salida.Result + error.Result);
            salida.Result.Should().MatchRegex(@"(?m)\bpass [1-9]\d*\s*$","node --test debe haber ejecutado casos (un 0 de 0 no prueba nada)");
        }
    }

    [Fact]
    public void El_selector_de_modal_del_teclado_de_la_cabecera_es_el_mismo_que_el_de_keytips_y_atajos_de_contexto()
    {
        var cabecera = Regex.Match(Leer("wwwroot", "js", "cabecera.js"), @"SELECTOR_MODAL\s*=\s*'([^']+)'");
        var keytips = Regex.Match(Leer("wwwroot", "js", "keytips.js"), @"SELECTOR_DIALOGO\s*=\s*'([^']+)'");
        cabecera.Success.Should().BeTrue();
        keytips.Success.Should().BeTrue();
        cabecera.Groups[1].Value.Should().Be(keytips.Groups[1].Value,
            "un <dialog> abierto con showModal() (ReconnectModal) o un alertdialog también es un modal: el teclado es suyo");
        // atajos-contexto.js lo parte en dos literales con el mismo contenido.
        var atajos = Regex.Replace(Leer("wwwroot", "js", "atajos-contexto.js"), @"\s+", " ");
        foreach (var parte in cabecera.Groups[1].Value.Split(", "))
            atajos.Should().Contain(parte);
        Regex.Replace(Leer("wwwroot", "js", "cabecera.js"), @"//.*", string.Empty)
            .Should().MatchRegex(@"document\.querySelector\(SELECTOR_MODAL\)\)\s*return", "la guarda existe");
    }

    [Fact]
    public void La_premisa_de_las_tablas_sigue_en_pie_su_cabecera_se_pega_al_borde_del_contenedor_que_desplaza()
    {
        // Si alguien cambia el top:0 por una altura de cabecera (la otra opción: desplazar el documento), este invariante sobra
        // y hay que revisarlo entero.
        var listas = Analizar(Leer("wwwroot", "css", "list-page.css"));
        listas.Should().Contain(r => r.Media == null && r.Selector == ".tabla-datos thead th"
            && Regex.IsMatch(r.Cuerpo, @"position\s*:\s*sticky") && Regex.IsMatch(r.Cuerpo, @"top\s*:\s*0\s*;"));
    }

    [Fact]
    public void El_detector_ve_cada_eslabon_que_falta()
    {
        const string bueno = ".shell { display: flex; height: 100vh; height: 100dvh; } "
            + ".area-principal { flex: 1; min-height: 0; } .contenido { flex: 1; min-height: 0; overflow-y: auto; }";

        DefectosDeLaCadena(bueno).Should().BeEmpty();
        DefectosDeLaCadena(bueno.Replace("height: 100vh; height: 100dvh;", "min-height: 100vh;"))
            .Should().Contain(d => d.StartsWith(".shell"), "el shell de antes (min-height) es justo el defecto");
        DefectosDeLaCadena(bueno.Replace("display: flex;", "display: flex; min-height: 100vh;"))
            .Should().Contain(d => d.Contains("vuelve a crecer"));
        DefectosDeLaCadena(bueno.Replace(".area-principal { flex: 1; min-height: 0; }", ".area-principal { flex: 1; }"))
            .Should().ContainSingle(d => d.StartsWith(".area-principal"));
        DefectosDeLaCadena(bueno.Replace(".contenido { flex: 1; min-height: 0;", ".contenido { flex: 1;"))
            .Should().ContainSingle(d => d.Contains("encoger por debajo"));
        DefectosDeLaCadena(bueno.Replace("overflow-y: auto;", string.Empty))
            .Should().ContainSingle(d => d.Contains("no es el contenedor que desplaza"));
        DefectosDeLaCadena(bueno.Replace(".contenido { flex: 1; min-height: 0; overflow-y: auto; }",
                ".contenido { flex: 1; min-height: 0; overflow-y: auto; } @media (max-width: 600px) { .contenido { overflow-y: visible; } }"))
            .Should().ContainSingle(d => d.Contains("@media (max-width: 600px)"), "un @media que suelte el overflow lo devuelve al documento");
        DefectosDeLaCadena(bueno + " @media (max-width: 1023px) { .shell { min-height: 100vh; height: auto; } }")
            .Should().ContainSingle(d => d.Contains("vuelve a tocar"));
        DefectosDeLaCadena(bueno + " @media (max-width: 1023px) { .shell { gap: 0; } }")
            .Should().BeEmpty("una regla de móvil que no toca altura ni overflow no rompe la cadena");
        DefectosDeLaCadena(bueno + " @media print { .shell { height: auto; } .contenido { overflow: visible; } }")
            .Should().BeEmpty("al imprimir sí se suelta la altura");
    }
}
