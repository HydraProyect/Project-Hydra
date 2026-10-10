using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Visitas = CaeManager.Web.Features.Visitas.Pages.Visitas;

namespace CaeManager.Web.Tests;

/// <summary>
/// Los filtros del listado de Visitas son pastillas de <c>BarraFiltros</c>: cada una es un menú cuyas
/// opciones (<c>menuitemradio</c>) viven en el panel que señala su <c>aria-controls</c>. «Solo activas»
/// y «Solo urgentes» ofrecen «No» y «Sí»; el orden está en «Más filtros». Varias pastillas repiten los
/// textos «Sí» y «No», así que la opción se busca siempre dentro de su pastilla.
/// </summary>
internal static class FiltrosVisitasDePrueba
{
    public static IElement Pastilla(IRenderedComponent<Visitas> cut, string etiqueta) =>
        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla")
            .Single(b => b.GetAttribute("aria-label") is { } nombre &&
                (nombre == etiqueta || nombre.StartsWith(etiqueta + ": ", StringComparison.Ordinal)));

    /// <summary>El panel del menú solo existe con la pastilla abierta: se abre si hace falta.</summary>
    public static IElement Opcion(IRenderedComponent<Visitas> cut, string etiqueta, string opcion)
    {
        if (Pastilla(cut, etiqueta).GetAttribute("aria-expanded") != "true")
            Pastilla(cut, etiqueta).Click();
        return cut.Find("#" + Pastilla(cut, etiqueta).GetAttribute("aria-controls")).QuerySelectorAll("[role=menuitemradio]")
            .Single(o => o.TextContent.Trim() == opcion);
    }

    public static void Elegir(IRenderedComponent<Visitas> cut, string etiqueta, string opcion) =>
        Opcion(cut, etiqueta, opcion).Click();

    public static Task ElegirAsync(IRenderedComponent<Visitas> cut, string etiqueta, string opcion) =>
        Opcion(cut, etiqueta, opcion).ClickAsync(new MouseEventArgs());
}
