using AngleSharp.Dom;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lo que comparten los tests «ResaltadoBusqueda» de los diez listados (cierre de listados H,
/// 2026-10-10): con texto en el buscador de la pantalla, la fila marca la coincidencia en las
/// celdas de los campos que su consulta busca, y solo cambia eso.
///
/// <para>
/// <b>Cómo se mide «no cambia».</b> Con el mismo render: se hace la <see cref="Foto"/> de la fila
/// sin búsqueda, se escribe en el buscador y se compara con la de después. La foto lleva el
/// <c>textContent</c> de la fila y todos sus <c>aria-label</c>, <c>title</c> y <c>alt</c>.
/// </para>
///
/// <para>
/// <b>Términos sin acento.</b> En cada página hay al menos un caso escrito sin el acento (o la
/// virgulilla) que lleva el texto de la celda: la marca tiene que contener el texto ORIGINAL. Para
/// que la fila llegue, los dobles de mediador que filtran por texto lo hacen con
/// <c>TextoDeBusqueda.Contiene</c>, el criterio en memoria de producción; los que usan los casos
/// de Centros y de Documentos devuelven las mismas filas con y sin búsqueda, y Proyectos filtra en
/// la propia página. Ningún doble reproduce el <c>unaccent</c> de PostgreSQL.
/// </para>
///
/// <para>
/// <b>Qué NO miden estos tests.</b> Qué campos mira cada doble (varios solo el nombre): los
/// términos son los que ese doble deja pasar. Tampoco ven el color.
/// </para>
/// </summary>
internal static class ResaltadoDeBusquedaEnListados
{
    private static readonly string[] AtributosDeNombre = ["aria-label", "aria-labelledby", "aria-describedby", "title", "alt"];

    /// <param name="Texto"><c>textContent</c> de la fila, tal cual.</param>
    /// <param name="Atributos">Una línea por atributo de nombre accesible o de título, en orden de documento.</param>
    internal sealed record FotoDeFila(string Texto, string Atributos);

    public static FotoDeFila Foto(this IElement fila) => new(
        fila.TextContent,
        string.Join('\n', fila.QuerySelectorAll("*").Prepend(fila).SelectMany(elemento => AtributosDeNombre
            .Where(elemento.HasAttribute)
            .Select(atributo => $"{elemento.LocalName}[{atributo}]={elemento.GetAttribute(atributo)}"))));

    /// <summary>El texto de cada <c>&lt;mark&gt;</c> de ese ámbito, en orden de documento.</summary>
    public static string[] Marcas(this IElement ambito) =>
        [.. ambito.QuerySelectorAll("mark").Select(marca => marca.TextContent)];

    /// <summary>La fila cuyo nombre (el botón <c>.enlace-nombre-fila</c>) es exactamente ese.</summary>
    public static IElement FilaDeNombre(this IRenderedComponent<IComponent> cut, string selectorDeFila, string nombre) =>
        cut.FindAll(selectorDeFila).Single(fila => fila.QuerySelectorAll(".enlace-nombre-fila").Any(boton => boton.TextContent.Trim() == nombre));

    /// <summary>
    /// Escribe en el buscador de la pantalla. Va por el <c>BusquedaChanged</c> de
    /// <see cref="BarraFiltros"/>, que es lo que dispara el campo cuando vence su rebote.
    /// </summary>
    public static Task BuscarEnLaBarraAsync(this IRenderedComponent<IComponent> cut, string termino) =>
        cut.InvokeAsync(() => cut.FindComponent<BarraFiltros>().Instance.BusquedaChanged.InvokeAsync(termino));

    /// <summary>
    /// La fila tiene exactamente esas marcas y, quitando eso, es la de <paramref name="antes"/>:
    /// mismo texto y mismos nombres accesibles y títulos.
    /// </summary>
    public static void DebeMarcarSolo(this IElement fila, FotoDeFila antes, params string[] marcas)
    {
        fila.Marcas().Should().Equal(marcas);

        var ahora = fila.Foto();
        ahora.Texto.Should().Be(antes.Texto, "la marca no añade ni quita un carácter al texto de la fila");
        ahora.Atributos.Should().Be(antes.Atributos, "aria-label, title y alt reciben la cadena íntegra, nunca la marca");
    }

    /// <summary>
    /// El identificador copiable de ese valor sigue entero: un solo <see cref="BotonCopiar"/> lo
    /// copia (<c>Valor</c>), lo pinta (<c>textContent</c>) y lo nombra, haya o no marca dentro.
    /// </summary>
    public static void DebeConservarElIdentificadorCopiable(this IRenderedComponent<IComponent> cut, string valor, bool conMarca)
    {
        var copiar = cut.FindComponents<BotonCopiar>().Single(boton => boton.Instance.Valor == valor);
        var boton = copiar.Find("button");

        boton.TextContent.Should().Be(valor);
        boton.GetAttribute("aria-label").Should().Contain(valor);
        boton.QuerySelectorAll("mark").Should().HaveCount(conMarca ? 1 : 0);
    }
}
