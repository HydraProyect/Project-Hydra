using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Elegir una opción de un <c>CampoSelectAvanzado</c> como lo hace una persona: abrir la lista y
/// pulsar la opción. Sustituye al <c>Change</c> que valía con el <c>&lt;select&gt;</c> nativo.
/// </summary>
internal static class SelectorAvanzadoAyuda
{
    public static async Task ElegirPorValorAsync<T>(this IRenderedComponent<T> cut, string idCampo, string valor) where T : IComponent
    {
        await cut.Find("#" + idCampo).ClickAsync(new MouseEventArgs());
        await cut.Find($"#{idCampo}-lista li[data-valor='{valor}']").ClickAsync(new MouseEventArgs());
    }

    public static IReadOnlyList<string> TextosDeLasOpciones<T>(this IRenderedComponent<T> cut, string idCampo) where T : IComponent =>
        cut.FindAll($"#{idCampo}-lista li[role='option']").Select(o => o.TextContent.Trim()).ToList();
}
