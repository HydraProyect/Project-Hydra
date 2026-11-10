using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// D-31 del recorrido en vivo del 2026-10-01: selector de tipo con búsqueda y agrupado, hoy un solo combobox en el que se
/// escribe y la lista se filtra; pestañas que envuelven; input de archivo con nombre.
/// </summary>
public class SelectorTipoDocumentoTests : BunitContext
{
    public SelectorTipoDocumentoTests() => Services.AddLocalization();

    private static TipoDocumentoListaDto Tipo(string nombre, RequisitoDocumental requisito, int orden, params string[] aliases) =>
        new(Guid.NewGuid(), nombre, null, false, orden, AmbitoAplicacion.Trabajador, requisito, NaturalezaJuridica.ObligacionLegal,
            null, null, null, null, false, false, false, default, aliases);

    private static IReadOnlyList<TipoDocumentoListaDto> Catalogo()
    {
        var tipos = new List<TipoDocumentoListaDto>
        {
            Tipo("Reconocimiento médico", RequisitoDocumental.Si, 1, "Aptitud médica"),
            Tipo("Formación PRL", RequisitoDocumental.Si, 2),
            Tipo("REA", RequisitoDocumental.Condicional, 3),
            Tipo("Certificado A1", RequisitoDocumental.Condicional, 4),
            Tipo("Otro documento", RequisitoDocumental.No, 5),
        };
        tipos.AddRange(Enumerable.Range(1, 5).Select(n => Tipo($"Extra {n}", RequisitoDocumental.No, 10 + n)));
        return tipos;
    }

    private readonly List<string> _elegidos = [];

    private IRenderedComponent<SelectorTipoDocumento> Renderizar(string valor = "", IReadOnlyList<TipoDocumentoListaDto>? tipos = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        return Render<SelectorTipoDocumento>(p => p.Add(c => c.Tipos, tipos ?? Catalogo()).Add(c => c.Valor, valor)
            .Add(c => c.ValorChanged, v => _elegidos.Add(v)));
    }

    private static IElement Disparador(IRenderedComponent<SelectorTipoDocumento> cut) => cut.Find("button[role=combobox]");

    private static IElement Buscador(IRenderedComponent<SelectorTipoDocumento> cut) => cut.Find("input.csa-buscador");

    private static async Task Abrir(IRenderedComponent<SelectorTipoDocumento> cut) => await Disparador(cut).ClickAsync(new MouseEventArgs());

    private static IReadOnlyList<string> Rotulos(IRenderedComponent<SelectorTipoDocumento> cut) =>
        cut.FindAll("[role=listbox] [role=group]").Select(g => g.QuerySelector(".csa-grupo-rotulo")!.TextContent.Trim()).ToList();

    private static IReadOnlyList<string> Opciones(IRenderedComponent<SelectorTipoDocumento> cut) =>
        cut.FindAll("[role=listbox] [role=option]").Select(o => o.TextContent.Trim()).ToList();

    [Fact]
    public async Task Es_un_solo_combobox_con_etiqueta_visible_sin_buscador_aparte()
    {
        var cut = Renderizar();

        cut.FindAll("select").Should().BeEmpty("el tipo ya no es un <select> nativo");
        cut.FindAll("input[type=text]").Should().BeEmpty("no hay un campo «Buscar tipo» separado de la lista");
        var disparador = Disparador(cut);
        cut.Find($"label[for='{disparador.Id}']").TextContent.Should().Be("Tipo de documento");
        disparador.TextContent.Trim().Should().Be("Selecciona un tipo de documento…");

        await Abrir(cut);

        var buscador = Buscador(cut);
        buscador.GetAttribute("role").Should().Be("combobox");
        buscador.GetAttribute("aria-controls").Should().Be(cut.Find("[role=listbox]").Id);
        buscador.GetAttribute("aria-activedescendant").Should().Be(cut.Find("[role=option].csa-activa").Id);
    }

    [Fact]
    public async Task Agrupa_por_peso_del_requisito_y_ordena_dentro_de_cada_grupo()
    {
        var cut = Renderizar();
        await Abrir(cut);

        Rotulos(cut).Should().Equal("Se piden siempre", "Según actividad o situación", "Otros");
        var primero = cut.FindAll("[role=listbox] [role=group]")[0];
        primero.GetAttribute("aria-labelledby").Should().Be(primero.QuerySelector(".csa-grupo-rotulo")!.Id);
        primero.QuerySelectorAll("[role=option]").Select(o => o.TextContent.Trim()).Should()
            .Equal("Reconocimiento médico", "Formación PRL");
    }

    [Fact]
    public async Task Escribir_filtra_dentro_de_cada_grupo_y_oculta_los_que_se_quedan_vacios()
    {
        var cut = Renderizar();
        await Abrir(cut);

        await Buscador(cut).InputAsync(new ChangeEventArgs { Value = "certificado" });

        Rotulos(cut).Should().Equal("Según actividad o situación");
        Opciones(cut).Should().Equal("Certificado A1");
    }

    [Theory]
    [InlineData("APTITUD")]
    [InlineData("medico")]
    [InlineData("formacion prl")]
    public async Task Busca_por_nombre_y_alias_sin_distinguir_mayusculas_ni_tildes(string consulta)
    {
        var cut = Renderizar();
        await Abrir(cut);

        await Buscador(cut).InputAsync(new ChangeEventArgs { Value = consulta });

        Opciones(cut).Should().ContainSingle().Which.Should().BeOneOf("Reconocimiento médico", "Formación PRL");
    }

    [Fact]
    public async Task Sin_coincidencias_lo_dice_y_no_pinta_ningun_grupo()
    {
        var cut = Renderizar();
        await Abrir(cut);

        await Buscador(cut).InputAsync(new ChangeEventArgs { Value = "zzz-no-existe" });

        Rotulos(cut).Should().BeEmpty();
        Opciones(cut).Should().BeEmpty();
        cut.Find(".csa-vacio").TextContent.Should().Be("Sin coincidencias");
    }

    [Fact]
    public async Task Escribir_sobre_el_campo_cerrado_lo_abre_y_empieza_a_filtrar()
    {
        var cut = Renderizar();

        await Disparador(cut).KeyDownAsync(new KeyboardEventArgs { Key = "X" });

        Disparador(cut).GetAttribute("aria-expanded").Should().Be("true");
        Buscador(cut).GetAttribute("value").Should().Be("X");
        Rotulos(cut).Should().Equal("Otros");
        Opciones(cut).Should().Equal("Extra 1", "Extra 2", "Extra 3", "Extra 4", "Extra 5");
    }

    [Fact]
    public async Task Con_flechas_e_Intro_la_eleccion_cruza_grupos_y_llega_al_formulario()
    {
        var catalogo = Catalogo();
        var cut = Renderizar(tipos: catalogo);
        await Abrir(cut);

        // Abre con la primera activa: tres abajo cruzan de «Se piden siempre» a «Certificado A1» y una arriba vuelve a «REA».
        for (var i = 0; i < 3; i++) await Buscador(cut).KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        await Buscador(cut).KeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" });
        await Buscador(cut).KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        _elegidos.Should().Equal(catalogo.Single(t => t.Nombre == "REA").Id.ToString());
        Disparador(cut).GetAttribute("aria-expanded").Should().Be("false");
    }

    [Theory]
    [InlineData("Escape")]
    [InlineData("Tab")]
    public async Task Escape_y_Tab_cierran_sin_cambiar_el_tipo(string tecla)
    {
        var cut = Renderizar();
        await Abrir(cut);
        await Buscador(cut).KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });

        await Buscador(cut).KeyDownAsync(new KeyboardEventArgs { Key = tecla });

        Disparador(cut).GetAttribute("aria-expanded").Should().Be("false");
        _elegidos.Should().BeEmpty();
    }

    [Fact]
    public async Task El_tipo_elegido_sigue_en_el_campo_mientras_se_busca_otro()
    {
        var catalogo = Catalogo();
        var elegido = catalogo.Single(t => t.Nombre == "Formación PRL");
        var cut = Renderizar(elegido.Id.ToString(), catalogo);
        await Abrir(cut);

        await Buscador(cut).InputAsync(new ChangeEventArgs { Value = "médico" });

        Disparador(cut).TextContent.Trim().Should().Be("Formación PRL");
        Opciones(cut).Should().Equal("Reconocimiento médico");
        _elegidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Con_pocos_tipos_la_lista_se_abre_sin_buscador()
    {
        var cut = Renderizar(tipos: Catalogo().Take(3).ToList());
        await Abrir(cut);

        cut.FindAll("input.csa-buscador").Should().BeEmpty();
        Opciones(cut).Should().HaveCount(3);
    }

    [Fact]
    public void El_input_de_archivo_de_la_zona_de_soltar_tiene_nombre_accesible()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = Render<ZonaSoltarArchivo>();

        cut.Find("input[type=file]").GetAttribute("aria-label").Should().Be("Suelta tu archivo aquí");
    }

    [Fact]
    public void Las_pestanas_envuelven_solo_si_se_pide()
    {
        IReadOnlyList<PestanaDefinicion> definiciones = [new("a", "Uno"), new("b", "Dos")];

        Render<Pestanas>(p => p.Add(c => c.Definiciones, definiciones).Add(c => c.PestanaActiva, "a"))
            .FindAll(".pestanas-lista-envuelta").Should().BeEmpty();
        Render<Pestanas>(p => p.Add(c => c.Definiciones, definiciones).Add(c => c.PestanaActiva, "a").Add(c => c.Envolver, true))
            .FindAll(".pestanas-lista-envuelta").Should().ContainSingle();
    }
}
