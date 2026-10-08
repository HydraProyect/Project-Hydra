using System.Globalization;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// La franja de estado (<c>FranjaEstado</c>) sustituyó a la pastilla de filtro «Estado» / «Documentación» y a su
/// chip (Listados 2/7). Los tests de los listados la manejan como la maneja quien la usa: por el rótulo del botón.
/// El rótulo es el texto del botón SIN su recuento, que va en un <c>&lt;b class="franja-estado-recuento"&gt;</c>
/// aparte: comparar con <c>TextContent</c> a secas mezclaría la cifra con el nombre.
/// </summary>
internal static class FranjaEstadoTestExtensions
{
    private const string Botones = ".franja-estado button.franja-estado-boton";

    /// <summary>El nombre del botón, sin la cifra: solo sus nodos de texto directos.</summary>
    public static string RotuloDeFranja(this IElement boton) =>
        string.Concat(boton.ChildNodes.Where(n => n.NodeType == NodeType.Text).Select(n => n.TextContent)).Trim();

    /// <summary>La cifra del botón; <c>null</c> si la franja no enseña recuentos.</summary>
    public static int? RecuentoDeFranja(this IElement boton) =>
        boton.QuerySelector(".franja-estado-recuento") is { } cifra
            ? int.Parse(cifra.TextContent.Trim(), CultureInfo.InvariantCulture)
            : null;

    /// <summary>El botón de la franja con ese rótulo exacto. Falla si no hay exactamente uno.</summary>
    public static IElement BotonDeFranja<TComponent>(this IRenderedComponent<TComponent> cut, string rotulo)
        where TComponent : IComponent =>
        cut.FindAll(Botones).Single(b => b.RotuloDeFranja() == rotulo);

    /// <summary>Los rótulos de la franja en el orden en que se pintan, con «Todos» / «Todas» delante.</summary>
    public static List<string> RotulosDeFranja<TComponent>(this IRenderedComponent<TComponent> cut)
        where TComponent : IComponent =>
        cut.FindAll(Botones).Select(b => b.RotuloDeFranja()).ToList();

    /// <summary>
    /// Los rótulos de los botones marcados (<c>aria-pressed="true"</c>). Sin ningún estado marcado es solo
    /// «Todos» / «Todas»: es lo que antes decía la ausencia del chip de estado.
    /// </summary>
    public static List<string> MarcadosEnFranja<TComponent>(this IRenderedComponent<TComponent> cut)
        where TComponent : IComponent =>
        cut.FindAll(Botones).Where(b => b.GetAttribute("aria-pressed") == "true").Select(b => b.RotuloDeFranja()).ToList();
}
