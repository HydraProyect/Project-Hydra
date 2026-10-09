using Bunit;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b (revisión Codex del lote A): cambiar de pestaña en el Context Workspace escribe
/// <c>?ctx=</c> con la pestaña nueva de la misma ficha. Un formulario del panel que vive
/// fuera de las pestañas (los drawers del panel de Centro) no se desmonta con eso, así que
/// su aviso no puede tomarlo por una salida: preguntaría sin motivo y «Seguir editando»
/// dejaría la URL desincronizada. Abrir otra ficha, cerrar el panel o salir de la página sí
/// son salidas.
/// </summary>
public class AvisoCambiosSinGuardarSincronizacionCtxTests : BunitContext
{
    private static readonly Guid Centro = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtroCentro = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static Uri U(string ruta) => new("http://localhost/" + ruta);

    [Theory]
    [InlineData("centros?ctx=Centro:{0}:informacion", "centros?ctx=Centro:{0}:documentacion", true)]
    [InlineData("centros?q=norte&ctx=Centro:{0}:informacion", "centros?ctx=Centro:{0}:agenda&q=norte", true)]
    [InlineData("centros?ctx=Centro:{0}:informacion", "centros?ctx=Centro:{1}:informacion", false)]
    [InlineData("centros?ctx=Centro:{0}:informacion", "centros", false)]
    [InlineData("centros?ctx=Centro:{0}:informacion", "trabajadores?ctx=Centro:{0}:documentacion", false)]
    [InlineData("centros?q=norte&ctx=Centro:{0}:informacion", "centros?q=sur&ctx=Centro:{0}:documentacion", false)]
    [InlineData("centros", "centros?ctx=Centro:{0}:informacion", false)]
    [InlineData("centros?ctx=Centro:{0}:informacion", "centros?ctx=Centro:{0}:informacion", false)]
    public void Solo_el_cambio_de_pestana_de_la_misma_ficha_es_sincronizacion(string actual, string destino, bool esperado)
    {
        ContextWorkspace.EsSoloCambioDePestana(
                U(string.Format(actual, Centro, OtroCentro)), U(string.Format(destino, Centro, OtroCentro)))
            .Should().Be(esperado);
    }

    private (IRenderedComponent<AvisoCambiosSinGuardar> Cut, NavigationManager Navegacion) RenderizarConCambiosEnFicha()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.NavigateTo($"centros?ctx=Centro:{Centro}:informacion");
        var cut = Render<AvisoCambiosSinGuardar>(p => p.Add(x => x.HayCambios, () => true));
        return (cut, navegacion);
    }

    [Fact]
    public async Task La_sincronizacion_de_la_pestana_no_pregunta()
    {
        var (cut, navegacion) = RenderizarConCambiosEnFicha();

        await cut.InvokeAsync(() => navegacion.NavigateTo($"centros?ctx=Centro:{Centro}:documentacion", replace: true));

        navegacion.Uri.Should().EndWith($"ctx=Centro:{Centro}:documentacion");
        cut.FindAll(".modal-contenido").Should().BeEmpty();
    }

    [Fact]
    public async Task Abrir_otra_ficha_con_cambios_pregunta()
    {
        var (cut, navegacion) = RenderizarConCambiosEnFicha();
        var origen = navegacion.Uri;

        await cut.InvokeAsync(() => navegacion.NavigateTo($"centros?ctx=Centro:{OtroCentro}:informacion"));

        navegacion.Uri.Should().Be(origen);
        cut.FindAll(".modal-contenido").Should().ContainSingle();
    }

    // --- La página de debajo escribe su pestaña o un filtro en la URL con la ficha abierta ---

    [Theory]
    [InlineData("trabajadores/x?ctx=Centro:{0}:informacion", "trabajadores/x?ctx=Centro:{0}:informacion&pestana=documentacion", true)]
    [InlineData("trabajadores/x?pestana=historial&ctx=Centro:{0}:informacion", "trabajadores/x?ctx=Centro:{0}:informacion", true)]
    [InlineData("centros?q=norte&ctx=Centro:{0}:informacion", "centros?q=sur&ctx=Centro:{0}:informacion", true)]
    [InlineData("centros?ctx=Centro:{0}:informacion", "centros?ctx=Centro:{0}:documentacion&pestana=x", false)]
    [InlineData("centros?ctx=Centro:{0}:informacion", "centros?ctx=Centro:{1}:informacion&pestana=x", false)]
    [InlineData("centros?ctx=Centro:{0}:informacion", "centros?pestana=x", false)]
    [InlineData("centros?pestana=y", "centros?pestana=x", false)]
    [InlineData("centros?ctx=Centro:{0}:informacion", "trabajadores?ctx=Centro:{0}:informacion", false)]
    public void La_ficha_sigue_abierta_solo_si_no_cambian_ni_la_pagina_ni_el_ctx(string actual, string destino, bool esperado)
    {
        ContextWorkspace.ConservaLaFichaAbierta(
                U(string.Format(actual, Centro, OtroCentro)), U(string.Format(destino, Centro, OtroCentro)))
            .Should().Be(esperado);
    }

    /// <summary>El aviso de un formulario de la ficha del Context Workspace: recibe el ámbito de ficha en cascada, como dentro del panel.</summary>
    private (IRenderedComponent<AvisoCambiosSinGuardar> Cut, NavigationManager Navegacion) RenderizarConCambiosDentroDeLaFicha()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.NavigateTo($"trabajadores/x?ctx=Centro:{Centro}:informacion");
        var cut = Render<AvisoCambiosSinGuardar>(p => p
            .Add(x => x.HayCambios, () => true)
            .AddCascadingValue(AmbitoCambiosSinGuardar.NombreAmbitoFicha, new AmbitoCambiosSinGuardar()));
        return (cut, navegacion);
    }

    [Fact]
    public async Task La_pestana_de_la_pagina_con_la_ficha_a_medio_editar_no_pregunta()
    {
        var (cut, navegacion) = RenderizarConCambiosDentroDeLaFicha();

        await cut.InvokeAsync(() => navegacion.NavigateTo($"trabajadores/x?ctx=Centro:{Centro}:informacion&pestana=documentacion", replace: true));

        navegacion.Uri.Should().EndWith("pestana=documentacion", "la ficha del panel sigue montada: la página solo apuntó su pestaña");
        cut.FindAll(".modal-contenido").Should().BeEmpty();
    }

    [Fact]
    public async Task Cerrar_la_ficha_a_medio_editar_sigue_preguntando_aunque_la_pagina_cambie_de_pestana()
    {
        var (cut, navegacion) = RenderizarConCambiosDentroDeLaFicha();
        var origen = navegacion.Uri;

        await cut.InvokeAsync(() => navegacion.NavigateTo("trabajadores/x?pestana=documentacion", replace: true));

        navegacion.Uri.Should().Be(origen);
        cut.FindAll(".modal-contenido").Should().ContainSingle();
    }

    /// <summary>Un formulario de la propia página (sin ámbito de ficha) sí puede desmontarse con un filtro o una pestaña de la página: sigue preguntando.</summary>
    [Fact]
    public async Task Un_formulario_de_la_pagina_sigue_preguntando_si_cambia_la_consulta_de_la_pagina()
    {
        var (cut, navegacion) = RenderizarConCambiosEnFicha();
        var origen = navegacion.Uri;

        await cut.InvokeAsync(() => navegacion.NavigateTo($"centros?ctx=Centro:{Centro}:informacion&pestana=documentacion", replace: true));

        navegacion.Uri.Should().Be(origen);
        cut.FindAll(".modal-contenido").Should().ContainSingle();
    }

    private static readonly IReadOnlyList<PestanaDefinicion> PestanasFicha =
    [
        new("informacion", "Información"),
        new("firma", "Firma"),
    ];

    /// <summary>
    /// Un formulario dentro de una pestaña (el aviso se registra en el ámbito de Pestanas),
    /// con Pestanas sincronizando ?ctx= como hace el Context Workspace.
    /// </summary>
    private (IRenderedComponent<Pestanas> Cut, NavigationManager Navegacion) RenderizarFormularioEnPestana()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.NavigateTo($"centros?ctx=Centro:{Centro}:informacion");
        var cut = Render<Pestanas>(p => p
            .Add(x => x.Definiciones, PestanasFicha)
            .Add(x => x.PestanaActiva, "informacion")
            .Add(x => x.PestanaActivaChanged, id => navegacion.NavigateTo($"centros?ctx=Centro:{Centro}:{id}", replace: true))
            .Add(x => x.ChildContent, b =>
            {
                b.OpenComponent<AvisoCambiosSinGuardar>(0);
                // Como la firma en campo: descartar no limpia nada, el contenido se desmonta después.
                b.AddAttribute(1, nameof(AvisoCambiosSinGuardar.HayCambios), (Func<bool>)(() => true));
                b.CloseComponent();
            }));
        return (cut, navegacion);
    }

    /// <summary>Revisión Codex: un enlace (o atrás/adelante) a otra pestaña de la misma ficha no ha pasado por Pestanas y sigue preguntando.</summary>
    [Fact]
    public async Task Otra_pestana_por_enlace_con_un_formulario_de_pestana_a_medias_pregunta()
    {
        var (cut, navegacion) = RenderizarFormularioEnPestana();
        var origen = navegacion.Uri;

        await cut.InvokeAsync(() => navegacion.NavigateTo($"centros?ctx=Centro:{Centro}:firma"));

        navegacion.Uri.Should().Be(origen);
        cut.FindAll(".modal-contenido").Should().ContainSingle();
    }

    /// <summary>Cambiar de pestaña con descarte ya confirmado: la sincronización de ?ctx= no vuelve a preguntar.</summary>
    [Fact]
    public async Task Tras_confirmar_el_cambio_de_pestana_la_sincronizacion_no_pregunta_otra_vez()
    {
        var (cut, navegacion) = RenderizarFormularioEnPestana();

        var clic = cut.FindAll(".pestanas-boton").First(b => b.TextContent.Trim() == "Firma").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Salir y descartar").ClickAsync(new MouseEventArgs());
        await clic;

        navegacion.Uri.Should().EndWith($"ctx=Centro:{Centro}:firma");
        cut.FindAll(".modal-contenido").Should().BeEmpty("la sincronización acompaña a un cambio ya confirmado");
    }
}
