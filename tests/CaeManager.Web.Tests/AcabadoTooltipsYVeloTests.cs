using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Acabado de interfaz, fichas 13 (tooltips propios) y 16 (desenfoque tras el velo).
/// <para>
/// Lo que estos tests observan: que todo <c>data-tooltip</c> del código vive en un
/// elemento que conserva su nombre accesible (<c>aria-label</c>) y ya no lleva el
/// <c>title</c> nativo, que el script declara el retardo, el rol y el enlace
/// <c>aria-describedby</c>, y que las hojas de Modal y Drawer declaran el desenfoque
/// detrás de <c>@supports</c>, bajo el token, anulado con transparencia o movimiento
/// reducidos. Lo que NO observan: cómo se ve, ni el coste del desenfoque; eso se mide en
/// el navegador.
/// </para>
/// </summary>
public class AcabadoTooltipsYVeloTests : BunitContext
{
    public AcabadoTooltipsYVeloTests() => Services.AddLocalization();

    [Fact]
    public void Todo_data_tooltip_conserva_aria_label_y_no_lleva_title_nativo()
    {
        var etiquetas = EtiquetasConDataTooltip().ToList();

        // Control positivo: el detector ve los siete botones de solo icono convertidos.
        etiquetas.Count.Should().BeGreaterThanOrEqualTo(7);

        etiquetas.Where(e => !Regex.IsMatch(e.Etiqueta, @"\baria-label\s*="))
            .Select(e => e.Fichero).Should().BeEmpty("un botón de solo icono sin aria-label se queda sin nombre accesible");
        etiquetas.Where(e => Regex.IsMatch(e.Etiqueta, @"\btitle\s*="))
            .Select(e => e.Fichero).Should().BeEmpty("el tooltip propio SUSTITUYE al title nativo, no convive con él");
    }

    [Fact]
    public void El_detector_ve_un_title_y_la_falta_de_aria_label_si_se_los_ponen()
    {
        // Sensibilidad del instrumento: sobre una etiqueta sintética con los dos defectos.
        var etiqueta = LeerEtiqueta("<button type=\"button\" title=\"x\" data-tooltip=\"x\" class=\"c\">", "data-tooltip");

        Regex.IsMatch(etiqueta, @"\btitle\s*=").Should().BeTrue();
        Regex.IsMatch(etiqueta, @"\baria-label\s*=").Should().BeFalse();
    }

    [Fact]
    public void El_boton_360_pinta_el_tooltip_sin_title_y_con_nombre_accesible_propio()
    {
        Services.AddSingleton<Microsoft.Extensions.Localization.IStringLocalizer<CaeManager.Web.Recursos.TextosComunes>>(
            new ClaveComoTexto());

        var cut = Render<Boton360>(p => p.Add(c => c.Nombre, "Obras Norte"));

        var boton = cut.Find("button.boton-360");
        boton.GetAttribute("data-tooltip").Should().NotBeNullOrWhiteSpace();
        boton.HasAttribute("title").Should().BeFalse();
        boton.GetAttribute("aria-label").Should().Contain("Obras Norte");
    }

    [Fact]
    public void El_script_del_tooltip_retarda_400_ms_usa_role_tooltip_y_enlaza_aria_describedby()
    {
        var js = Leer("wwwroot", "js", "tooltip.js");

        js.Should().Contain("RETARDO_MS = 400");
        js.Should().Contain("'role', 'tooltip'");
        js.Should().Contain("aria-describedby");
        js.Should().Contain(":focus-visible", "con teclado sale al enfocar, no tras un clic de ratón");
        js.Should().Contain("Escape");
        Leer("Components", "App.razor").Should().Contain("js/tooltip.js");
    }

    [Fact]
    public void La_hoja_global_del_tooltip_va_por_encima_de_modales_y_toasts_y_se_apaga_sin_movimiento()
    {
        var css = Leer("wwwroot", "css", "base.css");

        css.Should().MatchRegex(@"\.tooltip-propio\s*\{[^}]*z-index:\s*1150");
        css.Should().MatchRegex(@"\.tooltip-propio::after");
        css.Should().MatchRegex(@"@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{\s*\.tooltip-propio,\s*\.tooltip-propio\.tooltip-propio-visible\s*\{\s*transition:\s*none");
    }

    [Theory]
    [InlineData("Modal", "modal-superposicion")]
    [InlineData("Drawer", "drawer-superposicion")]
    public void El_velo_desenfoca_bajo_supports_y_token_y_se_anula_con_transparencia_o_movimiento_reducidos(string componente, string clase)
    {
        var css = Leer("Components", "DesignSystem", $"{componente}.razor.css");

        // Control positivo: el velo existe y sigue pintando el scrim.
        css.Should().MatchRegex($@"\.{clase}\s*\{{[^}}]*background-color:\s*var\(--color-scrim\)");

        css.Should().MatchRegex($@"@supports\s*\(backdrop-filter:\s*blur\(1px\)\)\s*\{{\s*\.{clase}\s*\{{\s*backdrop-filter:\s*var\(--scrim-filtro\)");
        css.Should().MatchRegex($@"@media\s*\(prefers-reduced-transparency:\s*reduce\),\s*\(prefers-reduced-motion:\s*reduce\)\s*\{{\s*\.{clase}\s*\{{\s*backdrop-filter:\s*none");
        // Ninguna otra regla del fichero fija un desenfoque a mano: todo pasa por el token.
        Regex.Matches(css, @"(?<!\()backdrop-filter:\s*blur\(").Count.Should().Be(0);
    }

    [Fact]
    public void El_token_del_desenfoque_esta_declarado_y_apagarlo_es_cambiar_una_linea()
    {
        var tokens = Leer("wwwroot", "css", "tokens.css");

        tokens.Should().MatchRegex(@"--scrim-filtro:\s*blur\(4px\);");
    }

    private static IEnumerable<(string Fichero, string Etiqueta)> EtiquetasConDataTooltip()
    {
        var raiz = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web");
        foreach (var fichero in Directory.EnumerateFiles(raiz, "*.razor", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var texto = File.ReadAllText(fichero);
            foreach (Match m in Regex.Matches(texto, @"\bdata-tooltip\s*="))
                yield return (Path.GetRelativePath(raiz, fichero), LeerEtiqueta(texto, "data-tooltip", m.Index));
        }
    }

    /// <summary>La etiqueta de apertura que contiene el índice: de su «&lt;» a su «&gt;», sin contar los de dentro de comillas.</summary>
    private static string LeerEtiqueta(string texto, string marca, int? indice = null)
    {
        var i = indice ?? texto.IndexOf(marca, StringComparison.Ordinal);
        var inicio = texto.LastIndexOf('<', i);
        char? comilla = null;
        var fin = i;
        for (; fin < texto.Length; fin++)
        {
            var c = texto[fin];
            if (comilla is null && (c == '"' || c == '\'')) comilla = c;
            else if (comilla == c) comilla = null;
            else if (comilla is null && c == '>') break;
        }
        return texto[inicio..Math.Min(fin + 1, texto.Length)];
    }

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer el fichero");
        return dir!;
    }

    private static string Leer(params string[] partes)
        => File.ReadAllText(Path.Combine([RaizDelRepositorio(), "src", "CaeManager.Web", .. partes]));

    /// <summary>Localizador de prueba: devuelve la clave (y la clave con el argumento) como texto.</summary>
    private sealed class ClaveComoTexto : Microsoft.Extensions.Localization.IStringLocalizer<CaeManager.Web.Recursos.TextosComunes>
    {
        public Microsoft.Extensions.Localization.LocalizedString this[string name]
            => new(name, name);

        public Microsoft.Extensions.Localization.LocalizedString this[string name, params object[] arguments]
            => new(name, $"{name} {string.Join(' ', arguments)}");

        public IEnumerable<Microsoft.Extensions.Localization.LocalizedString> GetAllStrings(bool includeParentCultures)
            => [];
    }
}
