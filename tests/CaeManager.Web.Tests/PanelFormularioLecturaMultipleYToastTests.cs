using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cierre del kit «Panel Formulario TALVEG»: modo Lectura (<see cref="DrawerLectura"/>), selección Múltiple de
/// <see cref="CampoSelectAvanzado"/> y toast de confirmación con enlace al registro creado.
/// </summary>
public class PanelFormularioLecturaMultipleYToastTests : BunitContext
{
    private static readonly IReadOnlyList<OpcionSelect> Delegaciones =
    [
        new("cv", "Comunidad Valenciana"),
        new("ct", "Cataluña"),
        new("md", "Madrid"),
    ];

    public PanelFormularioLecturaMultipleYToastTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    // ------------------------------------------------------------ Múltiple

    private IReadOnlyList<string> _elegidos = ["cv"];

    private IRenderedComponent<CampoSelectAvanzado> RenderizarMultiple(string? name = null) =>
        Render<CampoSelectAvanzado>(p => p
            .Add(x => x.Etiqueta, "Delegaciones")
            .Add(x => x.Id, "deleg")
            .Add(x => x.Opciones, Delegaciones)
            .Add(x => x.Marcador, "Añadir delegación")
            .Add(x => x.Multiple, true)
            .Add(x => x.Valores, _elegidos)
            .Add(x => x.ValoresChanged, v => _elegidos = v)
            .Add(x => x.Name, name));

    [Fact]
    public void Multiple_pinta_lo_elegido_como_chips_con_quitar_accesible()
    {
        var cut = RenderizarMultiple();

        var chip = cut.Find(".csa-chip");
        chip.TextContent.Should().Contain("Comunidad Valenciana");
        cut.Find(".csa-chip-quitar").GetAttribute("aria-label").Should().Be("Quitar Comunidad Valenciana");
        cut.Find("button#deleg .csa-valor").TextContent.Should().Be("Añadir delegación","el disparador no enseña lo elegido: ya está en los chips");
    }

    [Fact]
    public async Task Multiple_no_ofrece_lo_ya_elegido_y_elegir_anade_un_chip_y_cierra()
    {
        var cut = RenderizarMultiple();
        await cut.Find("button#deleg").ClickAsync();

        cut.FindAll("li[role=option]").Select(o => o.GetAttribute("data-valor")).Should().Equal("ct", "md");

        await cut.Find("li[data-valor='md']").ClickAsync();

        _elegidos.Should().Equal("cv", "md");
        cut.FindAll("div.csa-panel").Should().BeEmpty("al elegir se cierra el panel");
    }

    [Fact]
    public async Task Multiple_quita_un_elegido_con_su_equis()
    {
        _elegidos = ["cv", "ct"];
        var cut = RenderizarMultiple();

        await cut.FindAll(".csa-chip-quitar")[0].ClickAsync();

        _elegidos.Should().Equal("ct");
    }

    [Fact]
    public void Multiple_con_name_envia_un_campo_oculto_por_valor()
    {
        _elegidos = ["cv", "md"];
        var cut = RenderizarMultiple(name: "delegaciones");

        cut.FindAll("input[type=hidden][name=delegaciones]").Select(i => i.GetAttribute("value")).Should().Equal("cv", "md");
    }

    [Fact]
    public void Sin_Multiple_el_selector_simple_no_pinta_chips()
    {
        var cut = Render<CampoSelectAvanzado>(p => p.Add(x => x.Opciones, Delegaciones).Add(x => x.Valor, "ct"));

        cut.FindAll(".csa-chips").Should().BeEmpty();
        cut.Find(".csa-valor").TextContent.Should().Be("Cataluña");
    }

    // ------------------------------------------------------------ Lectura

    private IRenderedComponent<DrawerLectura> RenderizarLectura(bool soloConsulta = false, bool conEditar = true, string? href = "/trabajadores/1") =>
        Render<DrawerLectura>(p =>
        {
            p.Add(x => x.Visible, true)
             .Add(x => x.Titulo, "Carla Delgado Olmos")
             .Add(x => x.Subtitulo, "Empresa: Montajes Levante S.L.")
             .Add(x => x.HrefFicha, href)
             .Add(x => x.SoloConsulta, soloConsulta)
             .Add(x => x.ChildContent, (RenderFragment)(b =>
             {
                 b.OpenComponent<SeccionLectura>(0);
                 b.AddAttribute(1, nameof(SeccionLectura.Titulo), "Datos personales");
                 b.AddAttribute(2, nameof(SeccionLectura.ChildContent), (RenderFragment)(c =>
                 {
                     c.OpenComponent<DatoRejilla>(0);
                     c.AddAttribute(1, nameof(DatoRejilla.Etiqueta), "Teléfono");
                     c.AddAttribute(2, nameof(DatoRejilla.Valor), "600 000 000");
                     c.AddAttribute(3, nameof(DatoRejilla.EnLectura), true);
                     c.CloseComponent();
                     c.OpenComponent<DatoRejilla>(4);
                     c.AddAttribute(5, nameof(DatoRejilla.Etiqueta), "Correo");
                     c.AddAttribute(6, nameof(DatoRejilla.EnLectura), true);
                     c.CloseComponent();
                 }));
                 b.CloseComponent();
             }));
            if (conEditar) p.Add(x => x.OnEditar, () => { });
        });

    [Fact]
    public void Lectura_no_tiene_inputs_ni_pie_de_guardado_y_dice_Sin_dato_en_los_vacios()
    {
        var cut = RenderizarLectura();

        cut.FindAll("input, textarea, select").Should().BeEmpty();
        cut.Find("h3").TextContent.Should().Be("Datos personales");
        cut.FindAll("dt").Select(d => d.TextContent).Should().Equal("Teléfono", "Correo");
        cut.FindAll("dd").Select(d => d.TextContent.Trim()).Should().Equal("600 000 000", "Sin dato");
        cut.Markup.Should().NotContain("Guardar");
    }

    [Fact]
    public void Lectura_ofrece_abrir_ficha_completa_en_la_cabecera()
    {
        var cut = RenderizarLectura();

        var enlace = cut.Find("a.drawer-lectura-abrir");
        enlace.GetAttribute("href").Should().Be("/trabajadores/1");
        enlace.GetAttribute("aria-label").Should().Be("Abrir ficha completa");
        cut.Find("p, .drawer-subtitulo").TextContent.Should().Contain("Montajes Levante");
    }

    [Fact]
    public void Lectura_sin_href_no_pinta_abrir_ficha()
    {
        RenderizarLectura(href: null).FindAll("a.drawer-lectura-abrir").Should().BeEmpty();
    }

    [Fact]
    public async Task Lectura_del_Gestor_CAE_tiene_Cerrar_y_Editar()
    {
        var editado = false;
        var cerrado = false;
        var cut = Render<DrawerLectura>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.Titulo, "Carla")
            .Add(x => x.OnEditar, () => editado = true)
            .Add(x => x.VisibleChanged, v => cerrado = !v));

        var botones = cut.FindAll(".drawer-pie button");
        botones.Select(b => b.TextContent.Trim()).Should().Equal("Cerrar", "Editar");

        await botones[1].ClickAsync();
        editado.Should().BeTrue();

        await botones[0].ClickAsync();
        cerrado.Should().BeTrue();
    }

    [Fact]
    public void Lectura_de_Consulta_lleva_la_pastilla_y_ningun_pie()
    {
        var cut = RenderizarLectura(soloConsulta: true);

        cut.Find(".drawer-lectura-pastilla").TextContent.Should().Be("Solo consulta");
        cut.FindAll(".drawer-pie").Should().BeEmpty();
    }

    // ------------------------------------------------------------ Toast con enlace

    [Fact]
    public void Toast_con_enlace_es_clicable_entero_y_lleva_a_la_ficha()
    {
        var servicio = new ToastService();
        Services.AddSingleton(servicio);
        var cut = Render<AnfitrionToasts>();

        servicio.MostrarConEnlace("Trabajador creado correctamente.", new ToastEnlace("/trabajadores/42", "Ver ficha de Carla Delgado", "Carla Delgado"));
        cut.WaitForState(() => cut.FindAll(".toast").Count == 1);

        var enlace = cut.Find("a.toast-enlace");
        enlace.GetAttribute("href").Should().Be("/trabajadores/42");
        enlace.QuerySelector(".toast-enlace-titulo")!.TextContent.Should().Be("Trabajador creado correctamente.");
        enlace.QuerySelector(".toast-enlace-detalle")!.TextContent.Should().Be("Carla Delgado");
        enlace.QuerySelector(".toast-enlace-llamada")!.TextContent.Should().Contain("Ver ficha de Carla Delgado");
        cut.Find(".toast").ClassList.Should().Contain("toast-con-enlace");
        cut.FindAll(".toast-progreso").Should().BeEmpty("el toast con enlace no enseña la barra de tiempo");
    }

    [Fact]
    public void Toast_con_enlace_se_cierra_con_la_equis_sin_navegar()
    {
        var servicio = new ToastService();
        Services.AddSingleton(servicio);
        var cut = Render<AnfitrionToasts>();
        servicio.MostrarConEnlace("Creado", new ToastEnlace("/trabajadores/1", "Ver ficha"));
        cut.WaitForState(() => cut.FindAll(".toast").Count == 1);

        cut.Find("button.toast-cerrar").Click();

        cut.WaitForState(() => cut.FindAll(".toast").Count == 0);
        servicio.Mensajes.Should().BeEmpty();
    }

    [Fact]
    public async Task Toast_con_enlace_de_error_no_se_autodescarta()
    {
        var servicio = new ToastService(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30));
        servicio.MostrarConEnlace("Fallo", new ToastEnlace("/x", "Ver"), TonoToast.Error);

        await Task.Delay(250);

        servicio.Mensajes.Should().ContainSingle();
    }

    [Fact]
    public async Task Toast_con_enlace_no_se_oculta_solo_con_el_puntero_encima()
    {
        var servicio = new ToastService(TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(80));
        servicio.MostrarConEnlace("Creado", new ToastEnlace("/trabajadores/1", "Ver ficha"));
        var id = servicio.Mensajes.Single().Id;

        servicio.PunteroSobre(id, true);
        await Task.Delay(300);

        servicio.Mensajes.Should().ContainSingle();

        servicio.PunteroSobre(id, false);
        await Task.Delay(400);
        servicio.Mensajes.Should().BeEmpty();
    }
}
