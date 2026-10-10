using System.Globalization;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.AtajosGlobales;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Leyenda de teclado al pie de un listado (Cierre listados, línea H): una línea con las teclas que la
/// pantalla maneja y un rótulo de una palabra cada una. Las teclas salen de <see cref="CatalogoAtajos"/>,
/// no se escriben en el componente; la última entrada («?») remite a la chuleta, que es la que lo cuenta todo.
/// </summary>
public class LeyendaAtajosListaTests : BunitContext
{
    public LeyendaAtajosListaTests() => Services.AddLocalization();

    /// <summary>Lo que se lee al pie, entrada por entrada: sus teclas (una por <c>kbd</c>) y su rótulo.</summary>
    private static List<(string[] Teclas, string Rotulo)> Entradas(IRenderedComponent<LeyendaAtajosLista> cut) =>
        cut.FindAll("[data-pieza=leyenda-atajos] > li")
            .Select(li => (
                li.QuerySelectorAll("kbd").Select(k => k.TextContent).ToArray(),
                li.QuerySelector(".leyenda-atajos-lista-rotulo")!.TextContent))
            .ToList();

    private static List<string> TeclasPintadas(IRenderedComponent<LeyendaAtajosLista> cut) =>
        cut.FindAll("[data-pieza=leyenda-atajos] kbd").Select(k => k.TextContent).ToList();

    [Fact]
    public void Pinta_las_teclas_del_catalogo_en_el_orden_del_pie_con_su_rotulo_de_una_palabra()
    {
        var cut = Render<LeyendaAtajosLista>();

        Entradas(cut).Select(e => (string.Join(" ", e.Teclas), e.Rotulo)).Should().Equal(
            ("j k", "moverse"),
            ("Enter", "abrir"),
            ("e", "editar"),
            ("x", "marcar"),
            ("f", "filtrar"),
            ("Alt", "KeyTips"),
            ("?", "todos"));
    }

    /// <summary>
    /// La leyenda se deriva del catálogo: quien escriba una tecla a mano en el componente, o la cambie
    /// allí sin tocar <see cref="CatalogoAtajos"/>, deja de casar con él.
    /// </summary>
    [Fact]
    public void Cada_tecla_pintada_sale_de_CatalogoAtajos_y_ninguna_se_inventa()
    {
        var cut = Render<LeyendaAtajosLista>();

        var delCatalogo = CatalogoAtajos.Lista
            .Append(CatalogoAtajos.KeyTipsGestos.Single(a => a.Tecla == "Alt"))
            .Append(CatalogoAtajos.Acciones.Single(a => a.Tecla == "?"))
            .SelectMany(a => a.Tecla.Split(" / "))
            .ToList();

        TeclasPintadas(cut).Should().BeEquivalentTo(delCatalogo,
            "sin Omitir, la leyenda enseña todos los atajos de lista del catálogo, más Alt y ?, y nada más");
        cut.FindAll("[data-pieza=leyenda-atajos] > li").Select(li => li.GetAttribute("data-tecla"))
            .Should().Equal(CatalogoAtajos.LeyendaLista.Select(a => a.Tecla));
    }

    [Fact]
    public void LeyendaLista_no_anade_atajos_reutiliza_las_definiciones_de_sus_listas_de_origen()
    {
        var origen = CatalogoAtajos.Lista.Concat(CatalogoAtajos.KeyTipsGestos).Concat(CatalogoAtajos.Acciones).ToList();

        CatalogoAtajos.LeyendaLista.Should().OnlyContain(a => origen.Any(o => ReferenceEquals(o, a)));
        CatalogoAtajos.LeyendaLista.Should().Contain(CatalogoAtajos.Lista,
            "un atajo de lista nuevo que no entre en la leyenda la dejaría contando menos de lo que hay");
        CatalogoAtajos.LeyendaLista[^1].Tecla.Should().Be("?", "la última entrada remite a la chuleta");
    }

    [Fact]
    public void Omitir_quita_exactamente_esas_teclas()
    {
        var cut = Render<LeyendaAtajosLista>(p => p.Add(c => c.Omitir, ["x", "e"]));

        TeclasPintadas(cut).Should().Equal("j", "k", "Enter", "f", "Alt", "?");
    }

    [Theory]
    [InlineData("x")]
    [InlineData("e")]
    [InlineData("f")]
    public void Omitir_una_sola_tecla_deja_las_demas(string omitida)
    {
        var cut = Render<LeyendaAtajosLista>(p => p.Add(c => c.Omitir, [omitida]));

        TeclasPintadas(cut).Should().Equal(new[] { "j", "k", "Enter", "e", "x", "f", "Alt", "?" }.Where(t => t != omitida));
    }

    [Fact]
    public void Alt_y_la_interrogacion_van_siempre_aunque_se_omita_todo_lo_omitible()
    {
        var omitibles = CatalogoAtajos.Lista.Select(a => a.Tecla).ToList();

        var cut = Render<LeyendaAtajosLista>(p => p.Add(c => c.Omitir, omitibles));

        TeclasPintadas(cut).Should().Equal("Alt", "?");
    }

    /// <summary>
    /// «Alt», «?» o una errata («X») no son atajos de lista: omitirlos en silencio dejaría una leyenda que
    /// no dice lo que su pantalla cree que dice.
    /// </summary>
    [Theory]
    [InlineData("Alt")]
    [InlineData("?")]
    [InlineData("X")]
    [InlineData("j")]
    public void Omitir_una_tecla_que_no_esta_en_la_lista_del_catalogo_es_un_error(string tecla)
    {
        var pintar = () => Render<LeyendaAtajosLista>(p => p.Add(c => c.Omitir, [tecla]));

        pintar.Should().Throw<ArgumentException>().WithMessage($"*CatalogoAtajos.Lista*{tecla}*");
    }

    [Fact]
    public void Sin_filas_no_se_pinta()
    {
        var cut = Render<LeyendaAtajosLista>(p => p.Add(c => c.HayFilas, false));

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Es_una_lista_con_nombre_accesible_y_cada_tecla_va_en_kbd()
    {
        var cut = Render<LeyendaAtajosLista>();

        var lista = cut.Find("[data-pieza=leyenda-atajos]");
        lista.TagName.Should().Be("UL");
        lista.GetAttribute("aria-label").Should().Be("Atajos de teclado de esta lista");
        lista.Children.Should().OnlyContain(hijo => hijo.TagName == "LI");
        lista.Children.Should().OnlyContain(li => li.QuerySelectorAll("kbd").Length >= 1);
        cut.FindAll("[aria-hidden]").Should().BeEmpty("teclas y rótulos son información, no decoración");
    }

    /// <summary>
    /// El <c>title</c> de cada entrada es la descripción larga que ya da la chuleta, salvo en «?»: «Mostrar
    /// esta ayuda» solo se entiende dentro de ella.
    /// </summary>
    [Fact]
    public void El_title_reutiliza_la_descripcion_larga_del_catalogo()
    {
        var cut = Render<LeyendaAtajosLista>();

        cut.FindAll("[data-pieza=leyenda-atajos] > li").Select(li => li.GetAttribute("title")).Should().Equal(
            "Fila siguiente / anterior",
            "Abrir la fila enfocada",
            "Editar la fila enfocada desde su vista rápida",
            "Marcar/desmarcar la fila enfocada",
            "Ir al filtro de esta pantalla",
            "Pulsar y soltar: enseña una letra sobre cada control",
            "Ver todos los atajos de teclado");
    }

    [Fact]
    public void En_catalan_los_rotulos_y_el_nombre_accesible_salen_del_recurso_ca_ES()
    {
        var (cultura, culturaUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ca-ES");

            var cut = Render<LeyendaAtajosLista>();

            Entradas(cut).Select(e => e.Rotulo).Should().Equal("moure's", "obrir", "editar", "marcar", "filtrar", "KeyTips", "tots");
            cut.Find("[data-pieza=leyenda-atajos]").GetAttribute("aria-label").Should().Be("Dreceres de teclat d'aquesta llista");
            cut.Find("[data-pieza=leyenda-atajos]").GetAttribute("role").Should().Be("list",
                "con list-style: none hay lectores de pantalla que dejan de anunciarla como lista");
            cut.Find("[data-pieza=leyenda-atajos] > li[data-tecla='?']").GetAttribute("title")
                .Should().Be("Veure totes les dreceres de teclat", "el title propio de «?» también se traduce: sin clave en ca-ES caería al castellano sin avisar");
            TeclasPintadas(cut).Should().Equal("j", "k", "Enter", "e", "x", "f", "Alt", "?");
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (cultura, culturaUi);
        }
    }

    /// <summary>
    /// Todo rótulo breve existe en los dos recursos: una clave que falte no rompe nada, pinta la propia clave
    /// («ListaAbrirFilaBreve») al pie de diez pantallas.
    /// </summary>
    [Theory]
    [InlineData("es-ES")]
    [InlineData("ca-ES")]
    public void Ningun_rotulo_ni_title_pinta_su_clave_por_falta_de_recurso(string nombreCultura)
    {
        var (cultura, culturaUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(nombreCultura);

            var cut = Render<LeyendaAtajosLista>();

            var claves = CatalogoAtajos.LeyendaLista
                .SelectMany(a => new[] { a.ClaveDescripcion, a.ClaveDescripcion + LeyendaAtajosLista.SufijoRotuloBreve })
                .Append("LeyendaListaEtiqueta").Append("LeyendaTodosLosAtajos");
            foreach (var clave in claves)
            {
                cut.Markup.Should().NotContain(clave);
            }
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (cultura, culturaUi);
        }
    }
}
