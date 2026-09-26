using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b: los pasos repetidos de las pruebas de «cambios sin guardar» de cada formulario:
/// intentar salir y comprobar si <c>AvisoCambiosSinGuardar</c> detuvo la navegación.
/// </summary>
internal static class AvisoCambiosSinGuardarPrueba
{
    public const string DestinoFuera = "/trabajadores";

    /// <summary>Intenta salir a otra pantalla y comprueba que se detiene y pregunta.</summary>
    public static async Task SalirYComprobarQuePreguntaAsync<T>(this IRenderedComponent<T> cut, NavigationManager navegacion) where T : IComponent
    {
        var origen = navegacion.Uri;

        await cut.InvokeAsync(() => navegacion.NavigateTo(DestinoFuera));

        navegacion.Uri.Should().Be(origen, "con el formulario a medias la navegación se detiene");
        BotonDelAviso(cut, "Salir y descartar").Should().NotBeNull("el aviso tiene que estar preguntando");
    }

    /// <summary>Intenta salir y comprueba que no se pregunta: la navegación llega a su destino.</summary>
    public static async Task SalirYComprobarQueNoPreguntaAsync<T>(this IRenderedComponent<T> cut, NavigationManager navegacion, string porque) where T : IComponent
    {
        await cut.InvokeAsync(() => navegacion.NavigateTo(DestinoFuera));

        navegacion.Uri.Should().EndWith(DestinoFuera, porque);
        cut.FindAll(".modal-pie button").Should().NotContain(b => b.TextContent.Trim() == "Salir y descartar");
    }

    public static Task PulsarEnElAvisoAsync<T>(this IRenderedComponent<T> cut, string texto) where T : IComponent =>
        BotonDelAviso(cut, texto)!.ClickAsync(new MouseEventArgs());

    private static AngleSharp.Dom.IElement? BotonDelAviso<T>(IRenderedComponent<T> cut, string texto) where T : IComponent =>
        cut.FindAll(".modal-pie button").SingleOrDefault(b => b.TextContent.Trim() == texto);
}
