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

    /// <summary>
    /// D-05: pulsa el «Cancelar» del pie del Drawer o Modal abierto (<paramref name="pie"/> = <c>.drawer-pie</c> o <c>.modal-pie</c>).
    /// Con el formulario a medias, ese botón cierra como la X: pregunta «¿Descartar cambios?».
    /// </summary>
    public static Task PulsarCancelarDelPieAsync<T>(this IRenderedComponent<T> cut, string pie, string rotulo = "Cancelar") where T : IComponent =>
        cut.FindAll(pie + " button").Single(b => b.TextContent.Trim() == rotulo).ClickAsync(new MouseEventArgs());

    /// <summary>
    /// D-05: tras <see cref="PulsarCancelarDelPieAsync{T}"/> con cambios, comprueba que pregunta «¿Descartar cambios?» (y que lo
    /// escrito sigue en su sitio: <paramref name="contenedor"/> sigue abierto) y descarta para cerrar.
    /// </summary>
    public static async Task ComprobarQuePreguntaYDescartarAsync<T>(this IRenderedComponent<T> cut, string contenedor) where T : IComponent
    {
        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?", "«Cancelar» con cambios pregunta como la X");
        cut.FindAll(contenedor).Should().NotBeEmpty("hasta que se confirme, no se cierra");
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Descartar cambios").ClickAsync(new MouseEventArgs());
        cut.FindAll(contenedor).Should().BeEmpty("al descartar se cierra");
    }

    /// <summary>D-05: sin cambios, «Cancelar» cierra directamente y no pregunta.</summary>
    public static async Task ComprobarQueCancelarSinCambiosCierraAsync<T>(this IRenderedComponent<T> cut, string pie, string contenedor) where T : IComponent
    {
        await cut.PulsarCancelarDelPieAsync(pie);
        cut.FindAll(contenedor).Should().BeEmpty("sin cambios, Cancelar cierra directamente");
        cut.FindAll("h2").Should().NotContain(h => h.TextContent.Trim() == "¿Descartar cambios?");
    }

    public static Task PulsarEnElAvisoAsync<T>(this IRenderedComponent<T> cut, string texto) where T : IComponent =>
        BotonDelAviso(cut, texto)!.ClickAsync(new MouseEventArgs());

    private static AngleSharp.Dom.IElement? BotonDelAviso<T>(IRenderedComponent<T> cut, string texto) where T : IComponent =>
        cut.FindAll(".modal-pie button").SingleOrDefault(b => b.TextContent.Trim() == texto);
}
