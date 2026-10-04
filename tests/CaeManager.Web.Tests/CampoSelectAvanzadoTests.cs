using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CaeManager.Web.Tests;

/// <summary>
/// Contrato de <see cref="CampoSelectAvanzado"/>: patrón combobox/listbox de WAI-ARIA, teclado completo, búsqueda,
/// foco devuelto al cerrar y envío con <c>name</c>. El teclado vive en C#, así que se prueba aquí; lo que hace el JS
/// (preventDefault condicional, anclaje a la capa superior, volteo) lo cubre la verificación en navegador.
/// </summary>
public class CampoSelectAvanzadoTests : BunitContext
{
    private static readonly IReadOnlyList<OpcionSelect> Cuatro =
    [
        new("a", "Alta en la Seguridad Social", "Documento de alta o TA2", TonoBadge.Exito),
        new("b", "Aptitud médica", "Reconocimiento vigente"),
        new("c", "Bloqueada", Deshabilitada: true),
        new("d", "Certificado de Hacienda", "Corriente de pago"),
    ];

    private string _valor = "b";
    private readonly List<string> _cambios = [];

    public CampoSelectAvanzadoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private IRenderedComponent<CampoSelectAvanzado> Renderizar(
        IReadOnlyList<OpcionSelect>? opciones = null, bool? buscable = null, bool deshabilitado = false, string? name = null) =>
        Render<CampoSelectAvanzado>(p =>
        {
            p.Add(x => x.Etiqueta, "Tipo de documento")
             .Add(x => x.Id, "tipo")
             .Add(x => x.Opciones, opciones ?? Cuatro)
             .Add(x => x.Valor, _valor)
             .Add(x => x.ValorChanged, v => { _cambios.Add(v); _valor = v; })
             .Add(x => x.Deshabilitado, deshabilitado)
             .Add(x => x.Name, name);
            if (buscable is { } b) p.Add(x => x.Buscable, b);
        });

    private static IElement Disparador(IRenderedComponent<CampoSelectAvanzado> cut) => cut.Find("button#tipo");
    private static IElement Buscador(IRenderedComponent<CampoSelectAvanzado> cut) => cut.Find("input.csa-buscador");
    private static IElement Opcion(IRenderedComponent<CampoSelectAvanzado> cut, string valor) => cut.Find($"li[data-valor='{valor}']");

    private static Task Teclear(IElement el, string tecla) => el.KeyDownAsync(new KeyboardEventArgs { Key = tecla });

    private static string? Activa(IRenderedComponent<CampoSelectAvanzado> cut) =>
        cut.FindAll("li.csa-activa").SingleOrDefault()?.GetAttribute("data-valor");

    private static ElementReference Referencia(IRenderedComponent<CampoSelectAvanzado> cut, string campo) =>
        (ElementReference)typeof(CampoSelectAvanzado).GetField(campo, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cut.Instance)!;

    private string FocoPedido() => JSInterop.Invocations
        .Last(i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase))
        .Arguments[0].Should().BeOfType<ElementReference>().Which.Id!;

    // ------------------------------------------------------------ anatomía y ARIA

    [Fact]
    public void Cerrado_es_un_combobox_con_la_etiqueta_y_el_valor_actual()
    {
        var cut = Renderizar();

        var d = Disparador(cut);
        d.GetAttribute("role").Should().Be("combobox");
        d.GetAttribute("aria-haspopup").Should().Be("listbox");
        d.GetAttribute("aria-expanded").Should().Be("false");
        d.TextContent.Trim().Should().Be("Aptitud médica");
        cut.Find("label[for='tipo']").TextContent.Trim().Should().Be("Tipo de documento");
        cut.FindAll("[role=listbox]").Should().BeEmpty();
    }

    [Fact]
    public async Task Abierto_enlaza_disparador_y_lista_y_marca_la_opcion_actual()
    {
        var cut = Renderizar();

        await Disparador(cut).ClickAsync(new MouseEventArgs());

        var d = Disparador(cut);
        d.GetAttribute("aria-expanded").Should().Be("true");
        var lista = cut.Find("[role=listbox]");
        d.GetAttribute("aria-controls").Should().Be(lista.Id);
        lista.GetAttribute("aria-labelledby").Should().Be(cut.Find("label").Id);
        Opcion(cut, "b").GetAttribute("aria-selected").Should().Be("true");
        Opcion(cut, "a").GetAttribute("aria-selected").Should().Be("false");
        d.GetAttribute("aria-activedescendant").Should().Be(Opcion(cut, "b").Id, "la opción actual arranca como activa");
        Activa(cut).Should().Be("b");
    }

    [Fact]
    public async Task Cada_opcion_enseña_su_descripcion_y_su_punto_de_color()
    {
        var cut = Renderizar();
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        Opcion(cut, "a").QuerySelector(".csa-descripcion")!.TextContent.Should().Be("Documento de alta o TA2");
        Opcion(cut, "a").QuerySelector(".csa-punto-exito").Should().NotBeNull();
        Opcion(cut, "c").QuerySelector(".csa-descripcion").Should().BeNull("sin descripción no hay línea secundaria");
        Opcion(cut, "c").QuerySelector(".csa-punto").Should().BeNull("sin tono no hay punto");
        Opcion(cut, "c").GetAttribute("aria-disabled").Should().Be("true");
    }

    [Fact]
    public async Task Un_valor_sin_opcion_enseña_el_marcador_y_deshabilitado_no_abre()
    {
        _valor = "zzz";
        var cut = Renderizar();
        Disparador(cut).TextContent.Trim().Should().Be("Selecciona una opción");
        Disparador(cut).QuerySelector(".csa-marcador").Should().NotBeNull();

        var cerrado = Renderizar(deshabilitado: true);
        cerrado.Find("button#tipo").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Con_Name_un_input_oculto_lleva_el_valor_al_POST_nativo()
    {
        var cut = Renderizar(name: "tipoDocumento");

        var oculto = cut.Find("input[type=hidden][name=tipoDocumento]");
        oculto.GetAttribute("value").Should().Be("b");
        Renderizar().FindAll("input[type=hidden]").Should().BeEmpty("sin Name no se pinta ningún campo de formulario");
    }

    // ------------------------------------------------------------ teclado

    [Fact]
    public async Task Flechas_mueven_la_opcion_activa_omiten_la_deshabilitada_y_no_dan_la_vuelta()
    {
        var cut = Renderizar();
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        await Teclear(Disparador(cut), "ArrowDown");
        Activa(cut).Should().Be("d", "salta «c», que está deshabilitada");
        Disparador(cut).GetAttribute("aria-activedescendant").Should().Be(Opcion(cut, "d").Id);

        await Teclear(Disparador(cut), "ArrowDown");
        Activa(cut).Should().Be("d", "en el último no hay vuelta");

        await Teclear(Disparador(cut), "ArrowUp");
        await Teclear(Disparador(cut), "ArrowUp");
        Activa(cut).Should().Be("a");
        await Teclear(Disparador(cut), "ArrowUp");
        Activa(cut).Should().Be("a", "en el primero tampoco");
    }

    [Fact]
    public async Task Inicio_y_Fin_van_a_la_primera_y_a_la_ultima_habilitada()
    {
        var cut = Renderizar();
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        await Teclear(Disparador(cut), "End");
        Activa(cut).Should().Be("d");
        await Teclear(Disparador(cut), "Home");
        Activa(cut).Should().Be("a");
    }

    [Fact]
    public async Task Flecha_abajo_sobre_el_disparador_cerrado_abre_la_lista()
    {
        var cut = Renderizar();

        await Teclear(Disparador(cut), "ArrowDown");

        Disparador(cut).GetAttribute("aria-expanded").Should().Be("true");
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData(" ")]
    public async Task Intro_y_Espacio_eligen_la_activa_cierran_y_devuelven_el_foco(string tecla)
    {
        var cut = Renderizar();
        await Disparador(cut).ClickAsync(new MouseEventArgs());
        await Teclear(Disparador(cut), "ArrowDown");

        await Teclear(Disparador(cut), tecla);

        _cambios.Should().Equal(["d"]);
        cut.FindAll("[role=listbox]").Should().BeEmpty();
        Disparador(cut).GetAttribute("aria-expanded").Should().Be("false");
        FocoPedido().Should().Be(Referencia(cut, "_disparador").Id);
    }

    [Fact]
    public async Task Escape_cierra_sin_cambiar_el_valor_y_devuelve_el_foco()
    {
        var cut = Renderizar();
        await Disparador(cut).ClickAsync(new MouseEventArgs());
        await Teclear(Disparador(cut), "ArrowDown");

        await Teclear(Disparador(cut), "Escape");

        _cambios.Should().BeEmpty("Escape descarta lo que la flecha dejó activo");
        cut.FindAll("[role=listbox]").Should().BeEmpty();
        FocoPedido().Should().Be(Referencia(cut, "_disparador").Id);
    }

    [Fact]
    public async Task Tab_cierra_sin_elegir_y_sin_robar_el_foco()
    {
        var cut = Renderizar();
        await Disparador(cut).ClickAsync(new MouseEventArgs());
        await Teclear(Disparador(cut), "ArrowDown");
        var enfoquesAntes = JSInterop.Invocations.Count(i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase));

        await Teclear(Disparador(cut), "Tab");

        _cambios.Should().BeEmpty();
        cut.FindAll("[role=listbox]").Should().BeEmpty();
        JSInterop.Invocations.Count(i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase))
            .Should().Be(enfoquesAntes, "Tab sigue su camino: devolver el foco al disparador lo atraparía");
    }

    [Fact]
    public async Task Escribir_rapido_salta_a_la_opcion_que_empieza_por_lo_escrito()
    {
        var cut = Renderizar();
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        await Teclear(Disparador(cut), "c");
        Activa(cut).Should().Be("d", "la primera que empieza por c es «Certificado»");

        await Teclear(Disparador(cut), "a");
        Activa(cut).Should().Be("d", "«ca» no empieza ninguna opción: la activa no se mueve");
    }

    [Fact]
    public async Task Escribir_una_letra_con_la_lista_cerrada_la_abre_ya_posicionada()
    {
        var cut = Renderizar();

        await Teclear(Disparador(cut), "a");

        Disparador(cut).GetAttribute("aria-expanded").Should().Be("true");
        Activa(cut).Should().Be("a");
    }

    [Fact]
    public async Task Escribir_con_control_pulsado_no_es_escritura_rapida()
    {
        var cut = Renderizar();

        await Disparador(cut).KeyDownAsync(new KeyboardEventArgs { Key = "a", CtrlKey = true });

        Disparador(cut).GetAttribute("aria-expanded").Should().Be("false");
    }

    // ------------------------------------------------------------ ratón

    [Fact]
    public async Task Pulsar_una_opcion_la_elige_y_pulsar_la_actual_no_notifica()
    {
        var cut = Renderizar();
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        await Opcion(cut, "d").ClickAsync(new MouseEventArgs());

        _cambios.Should().Equal(["d"]);

        // Componente controlado: el padre devuelve el valor nuevo.
        cut.Render(p => p.Add(x => x.Valor, "d"));
        Disparador(cut).TextContent.Trim().Should().Be("Certificado de Hacienda");
        await Disparador(cut).ClickAsync(new MouseEventArgs());
        await Opcion(cut, "d").ClickAsync(new MouseEventArgs());
        _cambios.Should().Equal(["d"], "elegir lo que ya estaba elegido no es un cambio");
    }

    [Fact]
    public async Task Pulsar_una_opcion_deshabilitada_no_hace_nada()
    {
        var cut = Renderizar();
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        await Opcion(cut, "c").ClickAsync(new MouseEventArgs());

        _cambios.Should().BeEmpty();
        cut.FindAll("[role=listbox]").Should().HaveCount(1, "la lista sigue abierta");
    }

    [Fact]
    public async Task Pulsar_fuera_cierra_sin_cambiar_y_sin_devolver_el_foco()
    {
        var cut = Renderizar();
        await Disparador(cut).ClickAsync(new MouseEventArgs());
        var enfoquesAntes = JSInterop.Invocations.Count(i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase));

        await cut.Find(".csa-superposicion").ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=listbox]").Should().BeEmpty();
        _cambios.Should().BeEmpty();
        JSInterop.Invocations.Count(i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase)).Should().Be(enfoquesAntes);
    }

    // ------------------------------------------------------------ búsqueda

    [Fact]
    public async Task El_buscador_aparece_con_mas_de_ocho_opciones_o_si_se_pide_y_recibe_el_foco()
    {
        var pocas = Renderizar();
        await Disparador(pocas).ClickAsync(new MouseEventArgs());
        pocas.FindAll("input.csa-buscador").Should().BeEmpty("con cuatro opciones el buscador sobra");

        var muchas = Renderizar(Enumerable.Range(1, 9).Select(i => new OpcionSelect($"v{i}", $"Opción {i}")).ToList());
        await Disparador(muchas).ClickAsync(new MouseEventArgs());
        Buscador(muchas).GetAttribute("role").Should().Be("combobox");
        Buscador(muchas).GetAttribute("aria-controls").Should().Be(muchas.Find("[role=listbox]").Id);
        FocoPedido().Should().Be(Referencia(muchas, "_buscador").Id);

        var forzado = Renderizar(buscable: true);
        await Disparador(forzado).ClickAsync(new MouseEventArgs());
        forzado.FindAll("input.csa-buscador").Should().HaveCount(1);
    }

    [Fact]
    public async Task Buscar_filtra_por_texto_y_descripcion_sin_distinguir_mayusculas_ni_tildes()
    {
        var cut = Renderizar(buscable: true);
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        await Buscador(cut).InputAsync(new ChangeEventArgs { Value = "MEDICA" });
        cut.FindAll("li[role=option]").Select(o => o.GetAttribute("data-valor")).Should().Equal(["b"]);

        await Buscador(cut).InputAsync(new ChangeEventArgs { Value = "corriente" });
        cut.FindAll("li[role=option]").Select(o => o.GetAttribute("data-valor")).Should().Equal(["d"], "también busca en la descripción");

        await Buscador(cut).InputAsync(new ChangeEventArgs { Value = "zzz" });
        cut.FindAll("li[role=option]").Should().BeEmpty();
        cut.Find(".csa-vacio").TextContent.Should().Contain("Ninguna opción");
    }

    [Fact]
    public async Task Con_busqueda_el_foco_y_el_descendiente_activo_viven_en_el_campo_de_busqueda_y_Intro_elige()
    {
        var cut = Renderizar(buscable: true);
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        await Buscador(cut).InputAsync(new ChangeEventArgs { Value = "certificado" });
        Buscador(cut).GetAttribute("aria-activedescendant").Should().Be(Opcion(cut, "d").Id, "tras filtrar, la primera pasa a ser la activa");
        Disparador(cut).HasAttribute("aria-activedescendant").Should().BeFalse("con buscador el descendiente activo lo lleva el campo de búsqueda");

        await Teclear(Buscador(cut), "Enter");

        _cambios.Should().Equal(["d"]);
    }

    [Fact]
    public async Task En_el_buscador_Espacio_es_texto_e_Inicio_y_Fin_solo_navegan_con_el_campo_vacio()
    {
        var cut = Renderizar(buscable: true);
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        await Teclear(Buscador(cut), " ");
        _cambios.Should().BeEmpty("Espacio en el buscador se escribe, no elige");

        await Teclear(Buscador(cut), "End");
        Activa(cut).Should().Be("d", "con el campo vacío Fin va a la última");

        await Buscador(cut).InputAsync(new ChangeEventArgs { Value = "a" });
        await Teclear(Buscador(cut), "End");
        Activa(cut).Should().Be("a", "con texto escrito Fin mueve el cursor del campo: la activa no cambia");
    }

    // ------------------------------------------------------------ dentro de un Drawer

    [Fact]
    public async Task Dentro_de_un_DrawerFormulario_Escape_cierra_la_lista_y_no_el_drawer()
    {
        var visible = true;
        var cut = Render<DrawerFormulario>(p => p
            .Add(x => x.Visible, visible)
            .Add(x => x.VisibleChanged, v => visible = v)
            .Add(x => x.Titulo, "Alta")
            .Add(x => x.ChildContent, (RenderFragment)(b =>
            {
                b.OpenComponent<CampoSelectAvanzado>(0);
                b.AddAttribute(1, nameof(CampoSelectAvanzado.Id), "tipo");
                b.AddAttribute(2, nameof(CampoSelectAvanzado.Opciones), Cuatro);
                b.AddAttribute(3, nameof(CampoSelectAvanzado.Valor), _valor);
                b.CloseComponent();
            })));
        await cut.Find("button#tipo").ClickAsync(new MouseEventArgs());

        await cut.Find("button#tipo").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        cut.FindAll("[role=listbox]").Should().BeEmpty("el primer Escape cierra la lista");
        visible.Should().BeTrue("y no llega al Drawer");

        await cut.Find("button#tipo").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
        visible.Should().BeFalse("con la lista cerrada, Escape vuelve a ser del Drawer");
    }
}
