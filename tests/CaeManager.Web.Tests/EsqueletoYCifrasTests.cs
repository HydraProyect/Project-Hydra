using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Fichas 08 y 09. <c>Esqueleto</c>: la forma del contenido, con su retardo de 200 ms y sin brillo con movimiento reducido (como
/// <c>EstadoCargando</c>). <c>TarjetaMetrica.ContarHastaValor</c>: el valor final siempre está en el marcado (sin JS, prerender,
/// tests), la cuenta es del módulo JS, una sola vez y solo cuando se pide. bUnit no ejecuta el JS ni aplica CSS: que la cifra suba
/// de verdad se mide en el navegador.
/// </summary>
public class EsqueletoYCifrasTests : BunitContext
{
    public EsqueletoYCifrasTests() => Services.AddLocalization();

    [Theory]
    [InlineData(FormaEsqueleto.Lista, 6, ".esqueleto-fila-lista")]
    [InlineData(FormaEsqueleto.Tabla, 8, ".esqueleto-fila-tabla")]
    [InlineData(FormaEsqueleto.Metricas, 4, ".esqueleto-tarjeta")]
    public void Cada_forma_pinta_tantas_unidades_como_se_piden_y_se_anuncia_ocupada(FormaEsqueleto forma, int cantidad, string selector)
    {
        var cut = Render<Esqueleto>(p => p.Add(x => x.Forma, forma).Add(x => x.Cantidad, cantidad));

        cut.FindAll(selector).Should().HaveCount(cantidad);
        var raiz = cut.Find(".esqueleto");
        raiz.GetAttribute("aria-busy").Should().Be("true");
        raiz.GetAttribute("aria-label").Should().Be("Cargando…");
        raiz.ClassList.Should().Contain($"esqueleto-{forma.ToString().ToLowerInvariant()}");
    }

    [Fact]
    public void La_tabla_pinta_una_celda_por_columna()
    {
        var cut = Render<Esqueleto>(p => p.Add(x => x.Forma, FormaEsqueleto.Tabla).Add(x => x.Cantidad, 3).Add(x => x.Columnas, 5));

        cut.FindAll(".esqueleto-celda").Should().HaveCount(15);
        cut.Find(".esqueleto-fila-tabla").GetAttribute("style").Should().Contain("--esqueleto-columnas:5");
    }

    [Fact]
    public void La_hoja_retrasa_la_aparicion_y_apaga_el_brillo_con_movimiento_reducido()
    {
        var css = File.ReadAllText(Ruta("src", "CaeManager.Web", "Components", "DesignSystem", "Esqueleto.razor.css"));

        css.Should().MatchRegex(@"\.esqueleto\s*\{[^}]*animation:\s*esqueleto-aparece\s+0s\s+linear\s+200ms\s+both");
        css.Should().MatchRegex(@"@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{\s*\.esqueleto-bloque\s*\{\s*animation:\s*none");
        css.Should().Contain("var(--ease-fluid)");
    }

    [Fact]
    public void El_fundido_de_entrada_del_contenido_es_global_con_tokens_y_se_apaga_con_movimiento_reducido()
    {
        var css = File.ReadAllText(Ruta("src", "CaeManager.Web", "wwwroot", "css", "base.css"));

        css.Should().MatchRegex(@"\.contenido-entra\s*\{\s*animation:\s*contenido-entra\s+var\(--motion-transition\)\s+var\(--ease-fluid\)");
        css.Should().MatchRegex(@"@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{\s*\.contenido-entra\s*\{\s*animation:\s*none");
    }

    // ---- Cifras que cuentan ----

    [Fact]
    public void Con_ContarHastaValor_el_valor_final_esta_en_el_marcado_y_el_modulo_cuenta_una_sola_vez()
    {
        var modulo = JSInterop.SetupModule("./js/contar-cifra.js");
        modulo.Setup<bool>("contarHasta", _ => true).SetResult(true);

        var cut = Render<TarjetaMetrica>(p => p.Add(x => x.Etiqueta, "Vigentes").Add(x => x.Valor, "42").Add(x => x.ContarHastaValor, true));

        cut.Find(".tarjeta-metrica-valor").TextContent.Should().Be("42", "sin JS la cifra correcta ya está pintada");
        cut.WaitForAssertion(() => modulo.Invocations["contarHasta"].Should().ContainSingle()
            .Which.Arguments[1].Should().Be(42));

        // Un refresco (el valor cambia) no vuelve a animar: la cuenta es solo del primer pintado.
        cut.Render(p => p.Add(x => x.Valor, "57"));
        cut.Find(".tarjeta-metrica-valor").TextContent.Should().Be("57");
        modulo.Invocations["contarHasta"].Should().ContainSingle();
    }

    [Theory]
    [InlineData("42", false)] // sin pedirlo
    [InlineData("12 %", true)] // no es un entero
    [InlineData("-3", true)]
    public void Sin_pedirlo_o_sin_un_entero_no_se_importa_ni_el_modulo(string valor, bool contar)
    {
        JSInterop.Mode = JSRuntimeMode.Strict; // cualquier import sin preparar lanzaría

        var cut = Render<TarjetaMetrica>(p => p.Add(x => x.Etiqueta, "X").Add(x => x.Valor, valor).Add(x => x.ContarHastaValor, contar));

        cut.Find(".tarjeta-metrica-valor").TextContent.Should().Be(valor);
        JSInterop.Invocations.Should().BeEmpty();
    }

    [Fact]
    public void El_modulo_de_cuenta_respeta_el_movimiento_reducido_y_no_pisa_el_texto_de_blazor()
    {
        var js = File.ReadAllText(Ruta("src", "CaeManager.Web", "wwwroot", "js", "contar-cifra.js"));

        js.Should().Contain("prefers-reduced-motion: reduce");
        js.Should().Contain("nodeValue", "se escribe en el nodo de texto que Blazor guarda, no en textContent");
        js.Should().NotContain("textContent =");
        js.Should().Contain("dataset.contado", "una sola vez por tarjeta");
    }

    [Fact]
    public void Las_cifras_de_la_tarjeta_son_de_ancho_fijo()
    {
        File.ReadAllText(Ruta("src", "CaeManager.Web", "Components", "DesignSystem", "TarjetaMetrica.razor.css"))
            .Should().MatchRegex(@"\.tarjeta-metrica-valor\s*\{[^}]*font-variant-numeric:\s*tabular-nums");
    }

    [Theory]
    [InlineData("Features/Bandeja/Pages/MiTrabajo.razor", "<Esqueleto Forma=\"FormaEsqueleto.Lista\"")]
    [InlineData("Features/Bandeja/Pages/Bandeja.razor", "<Esqueleto Forma=\"FormaEsqueleto.Lista\"")]
    [InlineData("Features/Trabajadores/Pages/Trabajadores.razor", "<Esqueleto Forma=\"FormaEsqueleto.Tabla\"")]
    [InlineData("Features/Dashboard/Pages/Inicio.razor", "<Esqueleto Forma=\"FormaEsqueleto.Metricas\"")]
    public void Las_cuatro_pantallas_de_lista_mas_visitadas_pintan_la_forma_de_su_contenido_y_lo_hacen_entrar_con_fundido(string pantalla, string esqueleto)
    {
        var razor = File.ReadAllText(Ruta(new[] { "src", "CaeManager.Web" }.Concat(pantalla.Split('/')).ToArray()));

        razor.Should().Contain(esqueleto);
        razor.Should().Contain("contenido-entra", "el contenido real que sustituye al esqueleto entra con fundido");
    }

    [Fact]
    public void El_dashboard_cuenta_sus_cuatro_cifras_secundarias()
    {
        var razor = File.ReadAllText(Ruta("src", "CaeManager.Web", "Features", "Dashboard", "Pages", "Inicio.razor"));

        System.Text.RegularExpressions.Regex.Matches(razor, @"<TarjetaMetrica[^>]*ContarHastaValor=""true""").Should().HaveCount(4);
    }

    private static string Ruta(params string[] partes)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull();
        return Path.Combine(new[] { dir! }.Concat(partes).ToArray());
    }
}
