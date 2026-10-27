using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// CWE-602 («validación solo en el cliente»), extensión de la garantía de Boton a los campos del kit, al pie de los formularios
/// en Drawer y Modal y a PieListado: en Blazor Server quien quita <c>disabled</c> en el DOM consigue que el
/// evento llegue al manejador del servidor, así que el estado deshabilitado se vuelve a comprobar dentro del manejador. Cada test
/// reactiva el control como lo haría un atacante y dispara el evento; el llamador no se entera.
/// </summary>
public class KitCamposDeshabilitadosEnElServidorTests : BunitContext
{
    private static readonly IReadOnlyList<OpcionBuscable> Opciones = [new("1", "Uno"), new("2", "Dos")];

    public KitCamposDeshabilitadosEnElServidorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    [Fact]
    public async Task CampoTexto_deshabilitado_no_notifica_ni_al_escribir_ni_al_perder_el_foco()
    {
        var cambios = new List<string>();
        var blurs = 0;
        var cut = Render<CampoTexto>(p => p
            .Add(x => x.Deshabilitado, true)
            .Add(x => x.ValorChanged, v => cambios.Add(v))
            .Add(x => x.OnBlur, () => blurs++));
        var input = cut.Find("input");
        input.HasAttribute("disabled").Should().BeTrue();

        input.RemoveAttribute("disabled");
        await input.InputAsync(new ChangeEventArgs { Value = "intruso" });
        await input.BlurAsync(new FocusEventArgs());
        await Task.Delay(700);

        cambios.Should().BeEmpty();
        blurs.Should().Be(0);
    }

    [Fact]
    public async Task CampoTexto_habilitado_sigue_notificando_al_perder_el_foco()
    {
        var cambios = new List<string>();
        var cut = Render<CampoTexto>(p => p.Add(x => x.ValorChanged, v => cambios.Add(v)));
        var input = cut.Find("input");

        await input.InputAsync(new ChangeEventArgs { Value = "dato" });
        await input.BlurAsync(new FocusEventArgs());

        cambios.Should().Equal("dato");
    }

    [Fact]
    public async Task CampoTextarea_deshabilitado_no_notifica_ni_al_escribir_ni_al_perder_el_foco()
    {
        var cambios = new List<string>();
        var blurs = 0;
        var cut = Render<CampoTextarea>(p => p
            .Add(x => x.Deshabilitado, true)
            .Add(x => x.ValorChanged, v => cambios.Add(v))
            .Add(x => x.OnBlur, () => blurs++));
        var area = cut.Find("textarea");
        area.HasAttribute("disabled").Should().BeTrue();

        area.RemoveAttribute("disabled");
        await area.InputAsync(new ChangeEventArgs { Value = "intruso" });
        await area.BlurAsync(new FocusEventArgs());

        cambios.Should().BeEmpty();
        blurs.Should().Be(0);
    }

    [Fact]
    public async Task CampoTextarea_habilitado_notifica_y_avisa_del_blur()
    {
        var cambios = new List<string>();
        var blurs = 0;
        var cut = Render<CampoTextarea>(p => p
            .Add(x => x.ValorChanged, v => cambios.Add(v))
            .Add(x => x.OnBlur, () => blurs++));
        var area = cut.Find("textarea");

        await area.InputAsync(new ChangeEventArgs { Value = "dato" });
        await area.BlurAsync(new FocusEventArgs());

        cambios.Should().NotBeEmpty();
        cambios[^1].Should().Be("dato");
        blurs.Should().Be(1);
    }

    [Fact]
    public async Task CampoSelect_deshabilitado_no_notifica_un_cambio_que_llega_sin_el_atributo_disabled()
    {
        var cambios = new List<string>();
        var cut = Render<CampoSelect>(p => p
            .Add(x => x.Deshabilitado, true)
            .Add(x => x.ValorChanged, v => cambios.Add(v))
            .AddChildContent("<option value=\"a\">A</option><option value=\"b\">B</option>"));
        var select = cut.Find("select");
        select.HasAttribute("disabled").Should().BeTrue();

        select.RemoveAttribute("disabled");
        await select.ChangeAsync(new ChangeEventArgs { Value = "b" });

        cambios.Should().BeEmpty();
    }

    [Fact]
    public async Task CampoSelect_habilitado_notifica_el_cambio()
    {
        var cambios = new List<string>();
        var cut = Render<CampoSelect>(p => p
            .Add(x => x.ValorChanged, v => cambios.Add(v))
            .AddChildContent("<option value=\"a\">A</option><option value=\"b\">B</option>"));

        await cut.Find("select").ChangeAsync(new ChangeEventArgs { Value = "b" });

        cambios.Should().Equal("b");
    }

    [Fact]
    public async Task CampoBuscarSelect_deshabilitado_no_elige_aunque_el_texto_coincida_con_una_opcion()
    {
        var cambios = new List<string>();
        var cut = Render<CampoBuscarSelect>(p => p
            .Add(x => x.Opciones, Opciones)
            .Add(x => x.Deshabilitado, true)
            .Add(x => x.ValorChanged, v => cambios.Add(v)));
        var input = cut.Find("input");
        input.HasAttribute("disabled").Should().BeTrue();

        input.RemoveAttribute("disabled");
        await input.InputAsync(new ChangeEventArgs { Value = "Uno" });
        await Task.Delay(700);

        cambios.Should().BeEmpty();
    }

    [Fact]
    public async Task CampoBuscarSelect_habilitado_elige_la_coincidencia_exacta()
    {
        var cambios = new List<string>();
        var cut = Render<CampoBuscarSelect>(p => p
            .Add(x => x.Opciones, Opciones)
            .Add(x => x.ValorChanged, v => cambios.Add(v)));

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "Uno" });
        cut.WaitForAssertion(() => cambios.Should().Contain("1"), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task SelectorEntidad_deshabilitado_ignora_entrada_foco_teclas_y_creacion()
    {
        var cambios = new List<string>();
        var creaciones = new List<string>();
        var cut = Render<SelectorEntidad>(p => p
            .Add(x => x.Opciones, Opciones)
            .Add(x => x.Deshabilitado, true)
            .Add(x => x.PermiteCrear, true)
            .Add(x => x.ValorChanged, v => cambios.Add(v))
            .Add(x => x.OnCrearSolicitado, t => creaciones.Add(t)));
        var input = cut.Find("input");
        input.HasAttribute("disabled").Should().BeTrue();

        input.RemoveAttribute("disabled");
        await input.FocusAsync(new FocusEventArgs());
        await input.InputAsync(new ChangeEventArgs { Value = "Uno" });
        await input.KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        await input.KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        await input.BlurAsync(new FocusEventArgs());

        cambios.Should().BeEmpty();
        creaciones.Should().BeEmpty();
        cut.FindAll(".selector-entidad-opcion").Should().BeEmpty("el listado no se abre en un selector deshabilitado");
    }

    [Fact]
    public async Task SelectorEntidad_habilitado_elige_una_opcion_del_listado()
    {
        var cambios = new List<string>();
        var cut = Render<SelectorEntidad>(p => p
            .Add(x => x.Opciones, Opciones)
            .Add(x => x.ValorChanged, v => cambios.Add(v)));
        var input = cut.Find("input");

        await input.FocusAsync(new FocusEventArgs());
        await input.InputAsync(new ChangeEventArgs { Value = "Dos" });
        await cut.Find(".selector-entidad-opcion").ClickAsync(new MouseEventArgs());

        cambios.Should().Contain("2");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task PieListado_ignora_una_pagina_fuera_de_rango_aunque_el_boton_se_reactive(int paginaActual)
    {
        // Los botones Anterior y Siguiente están deshabilitados en los extremos; el servidor no se fía de eso.
        var paginas = new List<int>();
        var cut = Render<PieListado>(p => p
            .Add(x => x.PaginaActual, paginaActual == 4 ? 3 : 1)
            .Add(x => x.TotalPaginas, 3)
            .Add(x => x.TotalElementos, 60)
            .Add(x => x.PaginaActualChanged, n => paginas.Add(n)));
        var botones = cut.FindAll("button[aria-label]");
        var fueraDeRango = paginaActual == 4 ? botones[1] : botones[0];
        fueraDeRango.HasAttribute("disabled").Should().BeTrue();

        fueraDeRango.RemoveAttribute("disabled");
        await fueraDeRango.ClickAsync(new MouseEventArgs());

        paginas.Should().BeEmpty();
    }

    [Fact]
    public async Task PieListado_habilitado_cambia_de_pagina()
    {
        var paginas = new List<int>();
        var cut = Render<PieListado>(p => p
            .Add(x => x.PaginaActual, 2)
            .Add(x => x.TotalPaginas, 3)
            .Add(x => x.TotalElementos, 60)
            .Add(x => x.PaginaActualChanged, n => paginas.Add(n)));

        await cut.FindAll("button[aria-label]")[1].ClickAsync(new MouseEventArgs());

        paginas.Should().Equal(3);
    }

    [Fact]
    public async Task Un_Boton_en_PieExtra_de_DrawerFormulario_no_actua_mientras_se_guarda()
    {
        var clics = 0;
        var cut = Render<DrawerFormulario>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.Titulo, "Nuevo")
            .Add(x => x.Guardando, true)
            .Add(x => x.PieExtra, BotonDeFragmento(() => clics++))
            .Add(x => x.PieIzquierda, BotonDeFragmento(() => clics++)));

        foreach (var fieldset in cut.FindAll("fieldset")) fieldset.RemoveAttribute("disabled");
        var botones = cut.FindAll("fieldset button");
        botones.Should().HaveCount(2);
        foreach (var boton in botones)
        {
            boton.RemoveAttribute("disabled");
            await boton.ClickAsync(new MouseEventArgs());
        }

        clics.Should().Be(0);
    }

    [Fact]
    public async Task Un_Boton_en_PieExtra_de_DrawerFormulario_actua_cuando_no_se_guarda()
    {
        var clics = 0;
        var cut = Render<DrawerFormulario>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.Titulo, "Nuevo")
            .Add(x => x.PieExtra, BotonDeFragmento(() => clics++)));

        await cut.Find("fieldset button").ClickAsync(new MouseEventArgs());

        clics.Should().Be(1);
    }

    [Fact]
    public async Task Un_Boton_en_PieExtra_de_ModalFormulario_no_actua_mientras_se_guarda()
    {
        var clics = 0;
        var cut = Render<ModalFormulario>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.Titulo, "Nuevo")
            .Add(x => x.Guardando, true)
            .Add(x => x.PieExtra, BotonDeFragmento(() => clics++)));

        cut.Find("fieldset").RemoveAttribute("disabled");
        var boton = cut.Find("fieldset button");
        boton.RemoveAttribute("disabled");
        await boton.ClickAsync(new MouseEventArgs());

        clics.Should().Be(0);
    }

    private RenderFragment BotonDeFragmento(Action alPulsar) => b =>
    {
        b.OpenComponent<Boton>(0);
        b.AddComponentParameter(1, nameof(Boton.Variante), VarianteBoton.Secundario);
        b.AddComponentParameter(2, nameof(Boton.OnClick), EventCallback.Factory.Create(this, alPulsar));
        b.AddComponentParameter(3, nameof(Boton.ChildContent), (RenderFragment)(c => c.AddContent(0, "Extra")));
        b.CloseComponent();
    };
}
