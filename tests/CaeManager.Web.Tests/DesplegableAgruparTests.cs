using AngleSharp.Dom;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Desplegable «Agrupar» de un listado (pieza común de la línea «agrupar filas») y el ayudante que dice cómo
/// viaja la agrupación en la URL (<see cref="AgrupacionDeLista"/>). El teclado y el cierre del menú los hereda
/// de MenuAcciones (MenuAccionesTests); aquí se prueba lo propio: el rótulo, las opciones, cuál está marcada,
/// el aviso del cambio y la letra de KeyTips.
/// </summary>
public class DesplegableAgruparTests : BunitContext
{
    private static readonly IReadOnlyList<OpcionAgrupar> Opciones =
        [new("cliente", "Cliente"), new("empresa", "Empresa")];

    private readonly List<string?> _elegidos = [];

    public DesplegableAgruparTests()
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<DesplegableAgrupar> Renderizar(string? valor) =>
        Render<DesplegableAgrupar>(p => p
            .Add(c => c.Opciones, Opciones)
            .Add(c => c.Valor, valor)
            .Add(c => c.ValorChanged, EventCallback.Factory.Create<string?>(this, v => _elegidos.Add(v))));

    private static IElement Disparador(IRenderedComponent<DesplegableAgrupar> cut) => cut.Find(".menu-acciones-disparador");

    private static IReadOnlyList<IElement> Items(IRenderedComponent<DesplegableAgrupar> cut)
    {
        Disparador(cut).Click();
        return cut.FindAll("[role=menuitemradio]");
    }

    [Fact]
    public void Sin_agrupar_el_rotulo_dice_Agrupar_no_y_la_pastilla_no_esta_marcada()
    {
        var cut = Renderizar(valor: null);

        var disparador = Disparador(cut);
        disparador.TextContent.Trim().Should().Be("Agrupar: no");
        disparador.GetAttribute("aria-haspopup").Should().Be("menu");
        disparador.ClassList.Should().Contain("menu-acciones-disparador-pastilla").And.NotContain("menu-acciones-disparador-activa");
    }

    [Fact]
    public void Agrupado_el_rotulo_dice_el_campo_y_la_pastilla_se_marca()
    {
        var cut = Renderizar("cliente");

        var disparador = Disparador(cut);
        disparador.TextContent.Trim().Should().Be("Agrupar: Cliente");
        disparador.ClassList.Should().Contain("menu-acciones-disparador-activa");
    }

    /// <summary>La letra es la A de CatalogoAtajos.KeyTips: la declara el componente, no cada pantalla.</summary>
    [Fact]
    public void El_disparador_declara_la_letra_A_de_KeyTips()
    {
        Disparador(Renderizar("cliente")).GetAttribute("data-keytip").Should().Be("A");
    }

    [Theory]
    [InlineData(null, "Sin agrupar")]
    [InlineData("cliente", "Por Cliente")]
    [InlineData("empresa", "Por Empresa")]
    public void El_menu_ofrece_Sin_agrupar_y_una_opcion_por_campo_con_la_vigente_marcada(string? valor, string marcada)
    {
        var cut = Renderizar(valor);

        var items = Items(cut);

        items.Select(i => i.TextContent.Trim()).Should().Equal("Sin agrupar", "Por Cliente", "Por Empresa");
        items.Where(i => i.GetAttribute("aria-checked") == "true").Select(i => i.TextContent.Trim()).Should().Equal(marcada);
    }

    [Fact]
    public void Elegir_un_campo_avisa_con_su_clave_y_Sin_agrupar_avisa_con_null()
    {
        var cut = Renderizar("cliente");

        Items(cut).Single(i => i.TextContent.Trim() == "Por Empresa").Click();
        Items(cut).Single(i => i.TextContent.Trim() == "Sin agrupar").Click();

        _elegidos.Should().Equal("empresa", null);
    }

    [Theory]
    [InlineData(null, "Sin agrupar")]
    [InlineData("cliente", "Por Cliente")]
    public void Elegir_la_opcion_vigente_no_avisa(string? valor, string opcion)
    {
        var cut = Renderizar(valor);

        Items(cut).Single(i => i.TextContent.Trim() == opcion).Click();

        _elegidos.Should().BeEmpty();
    }

    /// <summary>Una clave que la pantalla no ofrece no puede rotularse: se lee como «sin agrupar».</summary>
    [Fact]
    public void Una_clave_que_no_esta_entre_las_opciones_se_pinta_como_sin_agrupar()
    {
        var cut = Renderizar("inventada");

        Disparador(cut).TextContent.Trim().Should().Be("Agrupar: no");
        Items(cut).Single(i => i.GetAttribute("aria-checked") == "true").TextContent.Trim().Should().Be("Sin agrupar");
    }

    // ------------------------------------------------- la agrupación en la URL (AgrupacionDeLista)

    /// <summary>Una pantalla que nace agrupada (Centros): ausente es la de fábrica y solo «no» la quita.</summary>
    [Theory]
    [InlineData(null, "cliente")]
    [InlineData("", "cliente")]
    [InlineData("no", null)]
    [InlineData("cliente", "cliente")]
    [InlineData("empresa", "empresa")]
    [InlineData("inventada", "cliente")]
    [InlineData("NO", "cliente")]
    public void Leer_con_agrupacion_de_fabrica(string? enLaUrl, string? esperada)
    {
        new AgrupacionDeLista("cliente", "cliente", "empresa").Leer(enLaUrl).Should().Be(esperada);
    }

    /// <summary>Una pantalla que nace sin agrupar: ausente, «no» y lo desconocido son lo mismo.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("no", null)]
    [InlineData("tipo", "tipo")]
    [InlineData("inventada", null)]
    public void Leer_sin_agrupacion_de_fabrica(string? enLaUrl, string? esperada)
    {
        new AgrupacionDeLista(null, "tipo").Leer(enLaUrl).Should().Be(esperada);
    }

    /// <summary>La vista de fábrica no deja rastro en la URL; lo demás viaja con su clave y sin agrupar, como «no».</summary>
    [Theory]
    [InlineData("cliente", "cliente", null)]
    [InlineData("cliente", null, "no")]
    [InlineData("cliente", "empresa", "empresa")]
    [InlineData(null, null, null)]
    [InlineData(null, "tipo", "tipo")]
    public void ParaUrl_solo_escribe_lo_que_se_aparta_de_la_fabrica(string? deFabrica, string? clave, string? esperado)
    {
        new AgrupacionDeLista(deFabrica, "cliente", "empresa", "tipo").ParaUrl(clave).Should().Be(esperado);
    }

    /// <summary>Lo que se escribe se vuelve a leer igual: quien guarde la vista solo necesita la URL.</summary>
    [Theory]
    [InlineData("cliente", "cliente")]
    [InlineData("cliente", null)]
    [InlineData("cliente", "empresa")]
    [InlineData(null, null)]
    [InlineData(null, "empresa")]
    public void Leer_lo_que_escribe_ParaUrl_devuelve_la_misma_agrupacion(string? deFabrica, string? clave)
    {
        var agrupacion = new AgrupacionDeLista(deFabrica, "cliente", "empresa");

        agrupacion.Leer(agrupacion.ParaUrl(clave)).Should().Be(clave);
    }
}
