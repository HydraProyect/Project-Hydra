using AngleSharp.Dom;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cómo elige una persona en un <see cref="CampoBuscarSelect"/>: abre el combobox, escribe en su buscador y pulsa la
/// opción de la lista filtrada. Antes era un input con datalist y bastaba con teclear el texto exacto; ahora el
/// texto solo filtra y lo que elige es pulsar la opción.
/// </summary>
internal static class AyudasCampoBuscarSelect
{
    public static IElement Disparador(IRenderedComponent<CampoBuscarSelect> campo) => campo.Find("button[role=combobox]");

    /// <summary>Texto visible en el campo cerrado: la opción elegida o, sin elección, el marcador.</summary>
    public static string TextoVisible(IRenderedComponent<CampoBuscarSelect> campo) => Disparador(campo).TextContent.Trim();

    public static async Task BuscarAsync(IRenderedComponent<CampoBuscarSelect> campo, string busqueda)
    {
        if (campo.FindAll("input.csa-buscador").Count == 0)
            await Disparador(campo).ClickAsync(new MouseEventArgs());
        await campo.Find("input.csa-buscador").InputAsync(new ChangeEventArgs { Value = busqueda });
    }

    /// <summary>Opciones que deja la búsqueda, por su texto visible.</summary>
    public static IReadOnlyList<IElement> Opciones(IRenderedComponent<CampoBuscarSelect> campo) => campo.FindAll("li[role=option]");

    /// <summary>Busca <paramref name="texto"/> y pulsa la opción cuyo texto es exactamente ese (la <paramref name="posicion"/>-ésima si hay varias).</summary>
    public static async Task ElegirAsync(IRenderedComponent<CampoBuscarSelect> campo, string texto, int posicion = 0)
    {
        await BuscarAsync(campo, texto);
        var opcion = Opciones(campo).Where(o => o.TextContent.Trim() == texto).ElementAt(posicion);
        await opcion.ClickAsync(new MouseEventArgs());
    }
}
