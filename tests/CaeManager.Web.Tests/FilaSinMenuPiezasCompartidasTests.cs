using Bunit;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Piezas compartidas del patrón de listados sin menú «⋯» (decisión 2026-10-08): la fila se
/// pulsa entera para abrir la vista rápida, y lo que vive dentro de ella —el icono 360 que
/// lleva a la página completa, el identificador copiable— no deja subir su clic. La fila
/// misma es marcado de cada página (el CSS aislado no cruza a un componente envoltorio), así
/// que lo que se comparte y se prueba aquí son sus piezas.
/// </summary>
public class FilaSinMenuPiezasCompartidasTests : BunitContext
{
    public FilaSinMenuPiezasCompartidasTests()
    {
        Services.AddLocalization();
        Services.AddSingleton<ToastService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>Una «fila» que cuenta los clics que le llegan, con la pieza dentro.</summary>
    private IRenderedComponent<ContenedorPulsable> EnFila(RenderFragment pieza) =>
        Render<ContenedorPulsable>(p => p.Add(c => c.ChildContent, pieza));

    private sealed class ContenedorPulsable : ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }
        public int Clics { get; private set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "fila-pulsable");
            builder.AddAttribute(2, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => Clics++));
            builder.AddContent(3, ChildContent);
            builder.CloseElement();
        }
    }

    // ------------------------------------------------------------- Boton360

    [Fact]
    public void Boton360_con_Href_es_un_enlace_a_la_pagina_360_con_nombre_accesible_propio()
    {
        var cut = Render<Boton360>(p => p.Add(c => c.Nombre, "Refrielectric S.A.").Add(c => c.Href, "/empresas/42"));

        var enlace = cut.Find("a.boton-360.boton-360-pagina");
        enlace.GetAttribute("href").Should().Be("/empresas/42");
        enlace.GetAttribute("aria-label").Should().Be("Abrir la ficha 360 de Refrielectric S.A.");
        enlace.GetAttribute("data-tooltip").Should().Be("Abrir la ficha 360");
        cut.FindAll("button").Should().BeEmpty("con Href no es el botón que abre el panel");
    }

    [Fact]
    public async Task Boton360_sin_Href_sigue_siendo_el_boton_que_abre_el_panel()
    {
        var pulsado = 0;
        var cut = Render<Boton360>(p => p.Add(c => c.Nombre, "Refrielectric S.A.").Add(c => c.OnClick, () => pulsado++));

        var boton = cut.Find("button.boton-360");
        boton.GetAttribute("aria-label").Should().Be("Consultar Refrielectric S.A. de un vistazo");
        boton.ClassList.Should().NotContain("boton-360-pagina");
        cut.FindAll("a").Should().BeEmpty();

        await boton.ClickAsync(new MouseEventArgs());
        pulsado.Should().Be(1);
    }

    // Lo que observa: que el giro del icono se declara sobre .boton-360, la clase que llevan las
    // dos variantes (botón del panel y enlace a la página), y que se anula con movimiento
    // reducido. Lo que NO observa: el giro pintado; eso se mide en el navegador.
    [Fact]
    public void El_icono_360_gira_en_las_dos_variantes_y_no_gira_con_movimiento_reducido()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        var hoja = File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Components", "DesignSystem", "Boton360.razor.css"));
        var partes = hoja.Split("@media (prefers-reduced-motion: reduce)");

        partes.Should().HaveCount(2);
        System.Text.RegularExpressions.Regex.IsMatch(partes[0],
            @"\.boton-360:hover ::deep svg,\s*\.boton-360:focus-visible ::deep svg\s*\{\s*transform:\s*rotate\(180deg\);").Should().BeTrue();
        System.Text.RegularExpressions.Regex.IsMatch(partes[1],
            @"\.boton-360:hover ::deep svg,\s*\.boton-360:focus-visible ::deep svg\s*\{\s*transform:\s*none;").Should().BeTrue();

        Render<Boton360>(p => p.Add(c => c.Nombre, "Refrielectric S.A.")).Find("button.boton-360").Should().NotBeNull();
        Render<Boton360>(p => p.Add(c => c.Nombre, "Refrielectric S.A.").Add(c => c.Href, "/empresas/42")).Find("a.boton-360").Should().NotBeNull();
    }

    [Fact]
    public async Task El_clic_en_el_icono_360_de_pagina_no_sube_a_la_fila()
    {
        var cut = EnFila(b =>
        {
            b.OpenComponent<Boton360>(0);
            b.AddAttribute(1, nameof(Boton360.Nombre), "Refrielectric S.A.");
            b.AddAttribute(2, nameof(Boton360.Href), "/empresas/42");
            b.CloseComponent();
        });

        await cut.Find(".fila-pulsable").ClickAsync(new MouseEventArgs());
        cut.Instance.Clics.Should().Be(1, "control positivo: la fila sí cuenta sus propios clics");

        // El enlace no tiene manejador propio (navega el navegador) y corta la subida: bUnit
        // lo dice con esta excepción —«nadie recibe este clic»—, que es justo la propiedad.
        // Sin el corte, el clic llegaría a la fila y no habría excepción (control de arriba).
        var clic = () => cut.Find("a.boton-360-pagina").ClickAsync(new MouseEventArgs());
        await clic.Should().ThrowAsync<MissingEventHandlerException>();
        cut.Instance.Clics.Should().Be(1, "ir a la página 360 no abre además la vista rápida");
    }

    // ---------------------------------------------------------- BotonCopiar

    private static RenderFragment Copiable(bool enLinea) => b =>
    {
        b.OpenComponent<BotonCopiar>(0);
        b.AddAttribute(1, nameof(BotonCopiar.Valor), "B-48.220.917");
        b.AddAttribute(2, nameof(BotonCopiar.Texto), "B-48.220.917");
        b.AddAttribute(3, nameof(BotonCopiar.Etiqueta), "el CIF");
        b.AddAttribute(4, nameof(BotonCopiar.NombreAccesible), "Copiar el CIF B-48.220.917");
        b.AddAttribute(5, nameof(BotonCopiar.EnLinea), enLinea);
        b.CloseComponent();
    };

    [Fact]
    public async Task El_identificador_copiable_en_linea_copia_sin_abrir_la_fila()
    {
        var cut = EnFila(Copiable(enLinea: true));

        var boton = cut.Find("button.boton-copiar.boton-copiar-en-linea");
        boton.TextContent.Trim().Should().Be("B-48.220.917");
        boton.GetAttribute("aria-label").Should().Be("Copiar el CIF B-48.220.917");
        boton.GetAttribute("title").Should().Be("Copiar el CIF B-48.220.917", "con el ratón encima dice qué hace el clic");

        await boton.ClickAsync(new MouseEventArgs());

        cut.Instance.Clics.Should().Be(0, "copiar no abre además la vista rápida");
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle()
            .Which.Mensaje.Should().Contain("el CIF");
    }

    /// <summary>
    /// El BotonCopiar de siempre (fuera de una fila) no cambia: sin la clase en línea, sin
    /// título y sin retener el clic, que era su contrato en los usos anteriores.
    /// </summary>
    [Fact]
    public async Task El_boton_copiar_que_no_va_en_linea_conserva_su_contrato()
    {
        var cut = EnFila(Copiable(enLinea: false));

        var boton = cut.Find("button.boton-copiar");
        boton.ClassList.Should().NotContain("boton-copiar-en-linea");
        boton.HasAttribute("title").Should().BeFalse();

        await boton.ClickAsync(new MouseEventArgs());
        cut.Instance.Clics.Should().Be(1, "sin EnLinea el clic sigue subiendo, como antes");
    }

    // ------------------------------------------------------ CabeceraListado

    [Fact]
    public void El_dato_de_cabecera_va_junto_al_titulo_detras_del_contador()
    {
        var cut = Render<CabeceraListado>(p => p
            .Add(c => c.Titulo, "Empresas")
            .Add(c => c.Contador, 12)
            .Add(c => c.DatoCabecera, (RenderFragment)(b => b.AddMarkupContent(0, "<span class=\"dato-de-prueba\">Gestor CAE</span>"))));

        // Por clase y no por identidad: bUnit envuelve cada elemento que devuelve.
        var hermanos = cut.Find(".cabecera-listado-contador").ParentElement!.Children.Select(h => h.ClassName).ToList();
        hermanos.Should().Contain("dato-de-prueba", "los dos van en el hueco de junto al título");
        hermanos.IndexOf("dato-de-prueba").Should().BeGreaterThan(hermanos.IndexOf("cabecera-listado-contador"),
            "el dato va detrás del contador");
    }

    [Fact]
    public void El_dato_de_cabecera_se_pinta_aunque_no_haya_contador()
    {
        var cut = Render<CabeceraListado>(p => p
            .Add(c => c.Titulo, "Empresas")
            .Add(c => c.DatoCabecera, (RenderFragment)(b => b.AddMarkupContent(0, "<span class=\"dato-de-prueba\">Gestor CAE</span>"))));

        cut.FindAll(".dato-de-prueba").Should().ContainSingle();
        cut.FindAll(".cabecera-listado-contador").Should().BeEmpty();
    }

    // ------------------------------------- ContextWorkspaceService: tecla «e»

    private static readonly Guid Ebro = Guid.NewGuid();
    private static readonly Guid Otra = Guid.NewGuid();

    [Fact]
    public async Task Abrir_en_edicion_abre_la_ficha_en_Informacion_avisa_y_la_peticion_es_de_un_solo_uso()
    {
        var servicio = new ContextWorkspaceService();
        var avisos = 0;
        servicio.OnEdicionSolicitada += () => avisos++;

        await servicio.AbrirEnEdicionAsync(EntidadWorkspace.Empresa, Ebro, "Montajes Ebro S.L.");

        servicio.FrameActual.Should().Be(new WorkspaceFrame(EntidadWorkspace.Empresa, Ebro, "Montajes Ebro S.L.", "informacion"));
        avisos.Should().Be(1);
        servicio.ConsumirEdicionSolicitada(EntidadWorkspace.Empresa, Otra).Should().BeFalse("la petición es de otra ficha");
        servicio.ConsumirEdicionSolicitada(EntidadWorkspace.Cliente, Ebro).Should().BeFalse("mismo Guid, otra ficha");
        servicio.ConsumirEdicionSolicitada(EntidadWorkspace.Empresa, Ebro).Should().BeTrue();
        servicio.ConsumirEdicionSolicitada(EntidadWorkspace.Empresa, Ebro).Should().BeFalse("ya se consumió");
    }

    [Fact]
    public async Task Abrir_la_ficha_sin_pedir_edicion_no_deja_ninguna_peticion()
    {
        var servicio = new ContextWorkspaceService();

        await servicio.AbrirAsync(EntidadWorkspace.Empresa, Ebro, "Montajes Ebro S.L.", "informacion");

        servicio.ConsumirEdicionSolicitada(EntidadWorkspace.Empresa, Ebro).Should().BeFalse();
    }

    /// <summary>
    /// Una petición que nadie atendió no sobrevive a su ficha: si no, reabrir más tarde la
    /// misma entidad entraría en edición sin que nadie lo pidiera.
    /// </summary>
    [Fact]
    public async Task Una_peticion_sin_atender_se_olvida_al_cambiar_de_ficha()
    {
        var servicio = new ContextWorkspaceService();
        await servicio.AbrirEnEdicionAsync(EntidadWorkspace.Empresa, Ebro, "Montajes Ebro S.L.");

        await servicio.AbrirAsync(EntidadWorkspace.Empresa, Otra, "Refrielectric S.A.", "informacion");
        await servicio.AbrirAsync(EntidadWorkspace.Empresa, Ebro, "Montajes Ebro S.L.", "informacion");

        servicio.ConsumirEdicionSolicitada(EntidadWorkspace.Empresa, Ebro).Should().BeFalse();
    }
}
