using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Contrato de <see cref="VentanaContexto"/> tras admitir elementos pulsables
/// (Listados 3/7, decisión del 2026-10-08).
///
/// <para>
/// Lo que aquí se prueba es el marcado y el cableado de eventos, que es lo que
/// bUnit observa. Que el panel siga abierto al cruzar el cursor del disparador
/// al panel, y que Esc lo cierre, vive en CSS (<c>:hover</c>, <c>:focus-within</c>)
/// y en <c>wwwroot/js/ventana-contexto.js</c>: bUnit no ejecuta ni uno ni otro,
/// así que de ese comportamiento estos tests solo fijan que las reglas y el
/// script existen y están enlazados, no que funcionen. Eso se comprobó en
/// navegador.
/// </para>
/// </summary>
public class VentanaContextoTests : BunitContext
{
    private static readonly string RaizWeb = Path.Combine(
        LocalizarRaizRepositorio(), "src", "CaeManager.Web");

    private static string LocalizarRaizRepositorio()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !Directory.Exists(Path.Combine(directorio.FullName, "src", "CaeManager.Web")))
            directorio = directorio.Parent;
        return directorio?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }

    private IRenderedComponent<VentanaContexto> Renderizar(
        bool interactiva, bool deshabilitada = false, string? titulo = "Incidencias", string? pie = null,
        RenderFragment? detalle = null) =>
        Render<VentanaContexto>(p => p
            .Add(c => c.Etiqueta, "2 documentos vencidos")
            .Add(c => c.Titulo, titulo)
            .Add(c => c.Pie, pie)
            .Add(c => c.Interactiva, interactiva)
            .Add(c => c.Deshabilitada, deshabilitada)
            .Add(c => c.Disparador, (RenderFragment)(b => b.AddContent(0, "2")))
            .Add(c => c.Detalle, detalle ?? (b => b.AddContent(0, "Aptitud médica — Sonia Cano"))));

    [Fact]
    public void Solo_lectura_conserva_el_contrato_anterior_contenedor_enfocable_con_role_note()
    {
        var cut = Renderizar(interactiva: false);

        var contenedor = cut.Find(".ventana-contexto");
        contenedor.GetAttribute("tabindex").Should().Be("0");
        contenedor.GetAttribute("role").Should().Be("note");
        contenedor.GetAttribute("aria-label").Should().Be("2 documentos vencidos");
        contenedor.ClassList.Should().NotContain("ventana-contexto-interactiva");
        cut.FindAll("button").Should().BeEmpty("una ventana de solo lectura no tiene nada que pulsar");
        cut.Find(".ventana-contexto-panel").GetAttribute("role").Should().Be("presentation");
    }

    [Fact]
    public void Interactiva_el_disparador_es_un_boton_y_el_panel_un_grupo_con_nombre()
    {
        var cut = Renderizar(interactiva: true);

        var contenedor = cut.Find(".ventana-contexto");
        contenedor.ClassList.Should().Contain("ventana-contexto-interactiva");
        // Dejó de ser un tooltip: el contenedor ya no es la parada de tabulación ni una nota.
        contenedor.HasAttribute("tabindex").Should().BeFalse();
        contenedor.HasAttribute("role").Should().BeFalse();

        var disparador = cut.Find("button.ventana-contexto-disparador");
        disparador.GetAttribute("type").Should().Be("button");
        disparador.GetAttribute("aria-label").Should().Be("2 documentos vencidos");
        disparador.TextContent.Should().Be("2");

        var panel = cut.Find(".ventana-contexto-panel");
        panel.GetAttribute("role").Should().Be("group");
        panel.GetAttribute("aria-label").Should().Be("Incidencias");
    }

    [Fact]
    public void Interactiva_sin_titulo_el_grupo_toma_el_nombre_de_la_etiqueta()
    {
        var cut = Renderizar(interactiva: true, titulo: null);

        cut.Find(".ventana-contexto-panel").GetAttribute("aria-label").Should().Be("2 documentos vencidos");
    }

    [Fact]
    public void Deshabilitada_gana_a_interactiva_no_hay_boton_ni_panel()
    {
        var cut = Renderizar(interactiva: true, deshabilitada: true);

        cut.Find(".ventana-contexto").ClassList.Should().Contain("ventana-contexto-inerte");
        cut.FindAll("button").Should().BeEmpty();
        cut.FindAll(".ventana-contexto-panel").Should().BeEmpty();
    }

    [Fact]
    public void El_pie_se_pinta_solo_si_se_da()
    {
        Renderizar(interactiva: true).FindAll(".ventana-contexto-pie").Should().BeEmpty();

        Renderizar(interactiva: true, pie: "Clic en una para corregirla aquí")
            .Find(".ventana-contexto-pie").TextContent.Should().Be("Clic en una para corregirla aquí");
    }

    [Theory]
    [InlineData(VentanaContextoColocacion.ArribaCentro, false, false)]
    [InlineData(VentanaContextoColocacion.AbajoCentro, true, false)]
    [InlineData(VentanaContextoColocacion.AbajoFin, true, true)]
    public void La_colocacion_se_traduce_en_clases(VentanaContextoColocacion colocacion, bool abajo, bool fin)
    {
        var cut = Render<VentanaContexto>(p => p
            .Add(c => c.Etiqueta, "3 centros")
            .Add(c => c.Colocacion, colocacion)
            .Add(c => c.Disparador, (RenderFragment)(b => b.AddContent(0, "3")))
            .Add(c => c.Detalle, (RenderFragment)(b => b.AddContent(0, "Sede Sevilla"))));

        var clases = cut.Find(".ventana-contexto").ClassList;
        clases.Contains("ventana-contexto-abajo").Should().Be(abajo);
        clases.Contains("ventana-contexto-fin").Should().Be(fin);
    }

    [Fact]
    public async Task Pulsar_un_elemento_avisa_al_llamador_y_no_abre_la_fila_que_contiene_la_ventana()
    {
        var pulsaciones = 0;
        var clicsEnFila = 0;

        // La ventana vive dentro de una fila pulsable, como en un listado.
        var cut = Render(b =>
        {
            b.OpenElement(0, "div");
            b.AddAttribute(1, "class", "fila");
            b.AddAttribute(2, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => clicsEnFila++));
            b.OpenComponent<VentanaContexto>(3);
            b.AddComponentParameter(4, nameof(VentanaContexto.Etiqueta), "1 documento vencido");
            b.AddComponentParameter(5, nameof(VentanaContexto.Interactiva), true);
            b.AddComponentParameter(6, nameof(VentanaContexto.Disparador), (RenderFragment)(d => d.AddContent(0, "1")));
            b.AddComponentParameter(7, nameof(VentanaContexto.Detalle), (RenderFragment)(d =>
            {
                d.OpenComponent<VentanaContextoElemento>(0);
                d.AddComponentParameter(1, nameof(VentanaContextoElemento.Ayuda), "Corregir sin salir de esta pantalla");
                d.AddComponentParameter(2, nameof(VentanaContextoElemento.AlPulsar), EventCallback.Factory.Create(this, () => pulsaciones++));
                d.AddComponentParameter(3, nameof(VentanaContextoElemento.ChildContent),
                    (RenderFragment)(c => c.AddContent(0, "Aptitud médica — Sonia Cano")));
                d.CloseComponent();
            }));
            b.CloseComponent();
            b.CloseElement();
        });

        var elemento = cut.Find("button.ventana-contexto-elemento");
        elemento.GetAttribute("type").Should().Be("button");
        elemento.GetAttribute("title").Should().Be("Corregir sin salir de esta pantalla");
        elemento.TextContent.Should().Contain("Aptitud médica — Sonia Cano");

        await elemento.ClickAsync(new MouseEventArgs());
        pulsaciones.Should().Be(1);
        clicsEnFila.Should().Be(0, "pulsar una incidencia no debe abrir además el panel de la fila");

        // Tampoco el propio disparador: abrir la ventana no es abrir la fila. El disparador no tiene
        // manejador propio y la propagación se corta en el contenedor, así que bUnit no encuentra a
        // quién entregar el clic: esa excepción es la observación de que no llegó a la fila.
        var clicEnDisparador = () => cut.Find("button.ventana-contexto-disparador").ClickAsync(new MouseEventArgs());
        await clicEnDisparador.Should().ThrowAsync<MissingEventHandlerException>();
        clicsEnFila.Should().Be(0);
    }

    [Fact]
    public async Task Solo_lectura_el_clic_sigue_llegando_a_la_fila()
    {
        var clicsEnFila = 0;
        var cut = Render(b =>
        {
            b.OpenElement(0, "div");
            b.AddAttribute(1, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => clicsEnFila++));
            b.OpenComponent<VentanaContexto>(2);
            b.AddComponentParameter(3, nameof(VentanaContexto.Etiqueta), "1 documento vencido");
            b.AddComponentParameter(4, nameof(VentanaContexto.Disparador), (RenderFragment)(d => d.AddContent(0, "1")));
            b.AddComponentParameter(5, nameof(VentanaContexto.Detalle), (RenderFragment)(d => d.AddContent(0, "ITV")));
            b.CloseComponent();
            b.CloseElement();
        });

        await cut.Find(".ventana-contexto").ClickAsync(new MouseEventArgs());

        clicsEnFila.Should().Be(1, "los usos de solo lectura no cambian: su clic burbujea como antes");
    }

    [Fact]
    public void Un_elemento_deshabilitado_no_se_puede_pulsar()
    {
        var cut = Render<VentanaContextoElemento>(p => p
            .Add(c => c.Deshabilitado, true)
            .Add(c => c.Secundario, "Aceptado")
            .AddChildContent("Norprevención"));

        cut.Find("button").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".ventana-contexto-elemento-secundario").TextContent.Should().Be("Aceptado");
    }

    // --- Lo que bUnit no ejecuta: se fija que existe y está enlazado ---

    [Fact]
    public void La_hoja_de_estilos_tiende_el_puente_abre_con_foco_dentro_y_respeta_el_cierre_por_Esc()
    {
        var css = File.ReadAllText(Path.Combine(RaizWeb, "Components", "DesignSystem", "VentanaContexto.razor.css"));

        css.Should().Contain(".ventana-contexto:focus-within > .ventana-contexto-panel",
            "sin :focus-within el panel se cerraría al tabular a uno de sus botones");
        css.Should().Contain(".ventana-contexto:not(.ventana-contexto-inerte):hover::after",
            "sin el puente el panel se cierra al cruzar la separación con el cursor");
        css.Should().Contain("[data-ventana-cerrada] > .ventana-contexto-panel",
            "es la regla con la que ventana-contexto.js cierra con Esc");
        css.Should().NotContain("dotted", "el disparador no lleva subrayado punteado (decisión del 2026-10-08)");
        css.Should().NotContain("text-decoration", "el disparador solo cambia de color al pasar el cursor");
    }

    [Fact]
    public void El_script_de_Esc_se_carga_con_la_aplicacion_y_usa_el_mismo_atributo_que_la_hoja()
    {
        var app = File.ReadAllText(Path.Combine(RaizWeb, "Components", "App.razor"));
        app.Should().Contain("js/ventana-contexto.js");

        var js = File.ReadAllText(Path.Combine(RaizWeb, "wwwroot", "js", "ventana-contexto.js"));
        js.Should().Contain("'Escape'").And.Contain("data-ventana-cerrada").And.Contain("stopImmediatePropagation");
    }
}
