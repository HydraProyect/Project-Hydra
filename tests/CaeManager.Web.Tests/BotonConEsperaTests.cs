using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Ficha 11 («Una sola espera»): el botón cuenta la espera. Dos instrumentos distintos, y cada uno dice lo que mide:
/// <list type="bullet">
/// <item><see cref="ClasificadorEspera"/> es una función pura: aquí se fijan los umbrales exactos (en el borde, no «más o menos»), de
/// modo que cambiar un <c>&gt;=</c> por <c>&gt;</c> o intercambiar los umbrales se ve en rojo (mutaciones del PR).</item>
/// <item>El componente se prueba con un <see cref="RelojManual"/> inyectado como <see cref="TimeProvider"/>: el tiempo avanza solo cuando
/// el test lo dice, con los umbrales reales, y nada depende de lo cargada que esté la máquina (un umbral de milisegundos contra el reloj
/// de pared dio un rojo intermitente en el gate). Se comprueba el marcado de cada estado, un latido antes y en el umbral, que no hay
/// doble envío y que Cancelar/Reintentar solo existen si el llamador los cablea.</item>
/// </list>
/// Lo que NO observan: el aspecto (bUnit no aplica CSS) ni el contraste, que miden <c>ContrasteDeComponentesPorTemaTests</c> y el
/// navegador.
/// </summary>
public class BotonConEsperaTests : BunitContext
{
    private static readonly TimeSpan Lenta = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan Colgada = TimeSpan.FromSeconds(12);

    private int _clics;
    private int _cancelados;
    private int _reintentos;

    public BotonConEsperaTests() => Services.AddLocalization();

    // ---- La regla ----

    [Theory]
    [InlineData(false, 99, 99, EstadoEspera.Reposo)]
    [InlineData(true, 0, 0, EstadoEspera.Guardando)]
    [InlineData(true, 3.999, 3.999, EstadoEspera.Guardando)]
    [InlineData(true, 4, 0, EstadoEspera.Lenta)] // el umbral de lentitud es inclusivo
    [InlineData(true, 11.999, 0, EstadoEspera.Lenta)]
    [InlineData(true, 12, 12, EstadoEspera.Colgada)] // y el de colgado
    [InlineData(true, 30, 11.999, EstadoEspera.Lenta)] // el colgado mide desde el último progreso, no desde el inicio
    [InlineData(true, 30, 12, EstadoEspera.Colgada)]
    public void El_clasificador_decide_en_el_borde_de_cada_umbral(bool enCurso, double transcurrido, double sinProgreso, EstadoEspera esperado)
    {
        ClasificadorEspera.Clasificar(enCurso, TimeSpan.FromSeconds(transcurrido), TimeSpan.FromSeconds(sinProgreso), Lenta, Colgada)
            .Should().Be(esperado);
    }

    [Fact]
    public void Los_umbrales_por_defecto_son_los_de_la_ficha_y_el_de_colgado_es_mayor_que_el_de_lentitud()
    {
        BotonConEspera.UmbralLentaPorDefecto.Should().Be(TimeSpan.FromSeconds(4));
        BotonConEspera.UmbralColgadaPorDefecto.Should().Be(TimeSpan.FromSeconds(12));
        BotonConEspera.UmbralColgadaPorDefecto.Should().BeGreaterThan(BotonConEspera.UmbralLentaPorDefecto,
            "colgada con el umbral por debajo del de lentitud nunca pasaría por «Tarda más de lo habitual»");
    }

    // ---- Marcado de cada estado ----

    private IRenderedComponent<BotonConEspera> Renderizar(Action<ComponentParameterCollectionBuilder<BotonConEspera>>? extra = null) =>
        Render<BotonConEspera>(p =>
        {
            p.Add(x => x.Variante, VarianteBoton.Primario)
             .Add(x => x.OnClick, EventCallback.Factory.Create(this, () => _clics++))
             .AddChildContent("Guardar");
            extra?.Invoke(p);
        });

    private static AngleSharp.Dom.IElement Boton(IRenderedComponent<BotonConEspera> cut) => cut.Find("button.boton-primario");

    [Fact]
    public async Task En_reposo_enseña_su_etiqueta_esta_habilitado_y_el_clic_llega()
    {
        var cut = Renderizar();

        Boton(cut).TextContent.Trim().Should().Be("Guardar");
        Boton(cut).HasAttribute("disabled").Should().BeFalse();
        cut.FindAll(".boton-espera-relleno").Should().BeEmpty();

        await Boton(cut).ClickAsync(new MouseEventArgs());
        _clics.Should().Be(1);
    }

    [Fact]
    public async Task Guardando_con_total_conocido_rellena_el_boton_con_su_porcentaje_y_no_deja_pulsar()
    {
        var cut = Renderizar(p => p.Add(x => x.Guardando, true).Add(x => x.Progreso, 42));

        cut.Find(".boton-espera-relleno").GetAttribute("style").Should().Contain("width:42%");
        Boton(cut).TextContent.Trim().Should().Be("Guardando… 42 %");
        Boton(cut).HasAttribute("disabled").Should().BeTrue("mientras corre, el botón no se puede pulsar");
        Boton(cut).GetAttribute("aria-busy").Should().Be("true");
        cut.Find(".boton-espera").HasAttribute("data-indeterminado").Should().BeFalse();

        // Sin doble envío: aunque el clic llegue antes del render deshabilitado, no sale al llamador.
        await Boton(cut).ClickAsync(new MouseEventArgs());
        _clics.Should().Be(0);
    }

    [Fact]
    public void Guardando_sin_total_es_una_franja_en_movimiento_sin_porcentaje()
    {
        var cut = Renderizar(p => p.Add(x => x.Guardando, true));

        cut.Find(".boton-espera-relleno").ClassList.Should().Contain("boton-espera-franja");
        Boton(cut).TextContent.Trim().Should().Be("Guardando…", "sin total no se inventa un porcentaje");
        cut.Find(".boton-espera").GetAttribute("data-indeterminado").Should().Be("true");
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(250, 100)]
    public void El_porcentaje_se_acota_a_cero_cien(int progreso, int esperado)
    {
        var cut = Renderizar(p => p.Add(x => x.Guardando, true).Add(x => x.Progreso, progreso));

        cut.Find(".boton-espera-relleno").GetAttribute("style").Should().Contain($"width:{esperado}%");
    }

    [Fact]
    public void La_region_de_estado_es_aria_live_y_esta_siempre_en_el_arbol()
    {
        var cut = Renderizar();

        var region = cut.Find(".boton-espera-mensaje");
        region.GetAttribute("aria-live").Should().Be("polite");
        region.GetAttribute("role").Should().Be("status");
    }

    // ---- Lenta, colgada y hecha: el tiempo lo avanza el test, no el reloj de pared ----

    private RelojManual Reloj { get; } = new();

    /// <summary>Avanza el reloj de la espera dentro del dispatcher del renderizador, como lo haría el temporizador real.</summary>
    private Task Avanzar(IRenderedComponent<BotonConEspera> cut, TimeSpan cuanto) => cut.InvokeAsync(() => Reloj.Avanzar(cuanto));

    private IRenderedComponent<BotonConEspera> RenderizarConReloj(Action<ComponentParameterCollectionBuilder<BotonConEspera>>? extra = null)
    {
        Services.AddSingleton<TimeProvider>(Reloj);
        return Renderizar(p =>
        {
            p.Add(x => x.Guardando, true);
            extra?.Invoke(p);
        });
    }

    private static string Estado(IRenderedComponent<BotonConEspera> cut) => cut.Find(".boton-espera").GetAttribute("data-estado")!;

    private static readonly TimeSpan Latido = BotonConEspera.IntervaloPorDefecto;

    [Fact]
    public async Task Pasado_el_umbral_avisa_de_que_tarda_y_ofrece_cancelar_si_el_llamador_puede()
    {
        var cut = RenderizarConReloj(p => p.Add(x => x.OnCancelarEspera, EventCallback.Factory.Create(this, () => _cancelados++)));

        await Avanzar(cut, BotonConEspera.UmbralLentaPorDefecto - Latido);
        Estado(cut).Should().Be("guardando", "un latido antes del umbral todavía no hay nada que avisar");
        cut.FindAll(".boton-espera-cancelar").Should().BeEmpty("al principio no hay nada que cancelar todavía");
        cut.Find(".boton-espera-mensaje").TextContent.Should().BeEmpty();

        await Avanzar(cut, Latido);

        Estado(cut).Should().Be("lenta");
        cut.Find(".boton-espera-mensaje").TextContent.Should().Be("Tarda más de lo habitual. No cierres la página.");
        cut.Find(".boton-espera-cancelar").Click();
        _cancelados.Should().Be(1);
    }

    [Fact]
    public async Task Sin_delegado_de_cancelar_no_se_ofrece_cancelar_aunque_tarde()
    {
        var cut = RenderizarConReloj();

        await Avanzar(cut, BotonConEspera.UmbralLentaPorDefecto);

        Estado(cut).Should().Be("lenta");
        cut.FindAll(".boton-espera-cancelar").Should().BeEmpty("prometer cancelar lo que no se puede interrumpir engaña");
    }

    [Fact]
    public async Task Colgada_con_reintento_pasa_a_Reintentar_en_aviso_y_el_clic_reintenta_sin_repetir_el_original()
    {
        var cut = RenderizarConReloj(p => p.Add(x => x.OnReintentar, EventCallback.Factory.Create(this, () => _reintentos++)));

        await Avanzar(cut, BotonConEspera.UmbralColgadaPorDefecto - Latido);
        Estado(cut).Should().Be("lenta", "un latido antes del umbral de colgado sigue siendo lenta");

        await Avanzar(cut, Latido);

        Estado(cut).Should().Be("colgada");
        Boton(cut).TextContent.Trim().Should().Be("Reintentar");
        Boton(cut).ClassList.Should().Contain("boton-espera-colgada");
        Boton(cut).HasAttribute("disabled").Should().BeFalse("colgada con reintento es lo único pulsable");
        cut.Find(".boton-espera-mensaje").TextContent.Should().Be("Sin respuesta desde hace 12 s. Lo que has escrito se conserva.");

        await Boton(cut).ClickAsync(new MouseEventArgs());
        _reintentos.Should().Be(1);
        _clics.Should().Be(0, "reintentar no es repetir el clic original");
    }

    [Fact]
    public async Task Colgada_sin_delegado_de_reintento_sigue_deshabilitada_pero_avisa()
    {
        var cut = RenderizarConReloj();

        await Avanzar(cut, BotonConEspera.UmbralColgadaPorDefecto);

        Estado(cut).Should().Be("colgada");
        Boton(cut).HasAttribute("disabled").Should().BeTrue("reintentar a ciegas un alta que quizá ya se hizo la duplicaría");
        Boton(cut).TextContent.Trim().Should().NotBe("Reintentar");
        cut.Find(".boton-espera-mensaje").TextContent.Should().Contain("Sin respuesta desde hace");
    }

    [Fact]
    public async Task Una_señal_de_progreso_aplaza_el_colgado()
    {
        // Colgada se mide desde el último progreso: con total conocido, cada avance es una señal de vida.
        var cut = RenderizarConReloj(p => p.Add(x => x.Progreso, 0));
        var casi = BotonConEspera.UmbralColgadaPorDefecto - TimeSpan.FromSeconds(1);

        await Avanzar(cut, casi);
        cut.Render(p => p.Add(x => x.Progreso, 10)); // señal de vida
        await Avanzar(cut, casi); // casi otro umbral entero desde la señal; casi el doble desde el inicio
        Estado(cut).Should().Be("lenta", "hubo señal hace menos del umbral: no cuelga aunque lleve el doble en total");

        await Avanzar(cut, TimeSpan.FromSeconds(1)); // y sin más avances, acaba colgando
        Estado(cut).Should().Be("colgada");
    }

    [Fact]
    public async Task Al_terminar_bien_dibuja_el_check_y_vuelve_a_la_etiqueta()
    {
        var cut = RenderizarConReloj();

        cut.Render(p => p.Add(x => x.Guardando, false));

        cut.Find(".boton-espera-check").Should().NotBeNull();
        Boton(cut).TextContent.Trim().Should().Be("Guardado");
        Boton(cut).ClassList.Should().Contain("boton-espera-hecha");

        await Avanzar(cut, BotonConEspera.DuracionHechaPorDefecto - Latido);
        cut.FindAll(".boton-espera-check").Should().NotBeEmpty("un latido antes de que venza, el check sigue");

        await Avanzar(cut, Latido * 2); // el latido que cruza DuracionHecha

        cut.FindAll(".boton-espera-check").Should().BeEmpty();
        Boton(cut).TextContent.Trim().Should().Be("Guardar");
        Reloj.TemporizadoresVivos.Should().Be(0, "al volver al reposo el componente suelta su temporizador");
    }

    // ---- Hecha (con error) ----

    [Fact]
    public void Al_terminar_con_error_no_hay_check_y_el_boton_vuelve_a_estar_pulsable()
    {
        var cut = Renderizar(p => p.Add(x => x.Guardando, true));

        cut.Render(p => p.Add(x => x.Guardando, false).Add(x => x.Fallido, true));

        cut.FindAll(".boton-espera-check").Should().BeEmpty("un guardado que falló no se celebra");
        Boton(cut).TextContent.Trim().Should().Be("Guardar");
        Boton(cut).HasAttribute("disabled").Should().BeFalse("el usuario puede corregir y volver a guardar: lo escrito se conserva");
    }

    [Fact]
    public void Una_pantalla_que_deshabilita_el_boton_lo_sigue_deshabilitando_en_reposo()
    {
        var cut = Renderizar(p => p.Add(x => x.Deshabilitado, true).AddUnmatched("title", "Falta el nombre"));

        Boton(cut).HasAttribute("disabled").Should().BeTrue();
        Boton(cut).GetAttribute("title").Should().Be("Falta el nombre", "los atributos sueltos (el motivo) llegan al botón");
    }

    // ---- Hoja de estilos ----

    [Fact]
    public void La_hoja_usa_tokens_de_movimiento_y_se_apaga_con_movimiento_reducido()
    {
        var css = File.ReadAllText(RutaDelComponente("BotonConEspera.razor.css"));

        css.Should().Contain("var(--motion-").And.Contain("var(--ease-fluid)");
        var reducido = css[css.IndexOf("@media (prefers-reduced-motion: reduce)", StringComparison.Ordinal)..];
        reducido.Should().Contain("animation: none").And.Contain("transition: none");
    }

    [Fact]
    public void El_relleno_usa_los_colores_de_hover_que_el_trinquete_de_contraste_ya_mide()
    {
        // El relleno no tiene par propio en ContrasteDeComponentesPorTemaTests: su garantía es ser el MISMO fondo que el hover de cada variante.
        var css = File.ReadAllText(RutaDelComponente("BotonConEspera.razor.css"));

        css.Should().MatchRegex(@"\.boton-espera-relleno\s*\{[^}]*background-color:\s*var\(--color-primario-fondo-hover\)");
        css.Should().MatchRegex(@"\.boton-fantasma \.boton-espera-relleno\s*\{[^}]*background-color:\s*var\(--color-surface-hover\)");
    }

    private static string RutaDelComponente(string fichero)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull();
        return Path.Combine(dir!, "src", "CaeManager.Web", "Components", "DesignSystem", fichero);
    }
}
