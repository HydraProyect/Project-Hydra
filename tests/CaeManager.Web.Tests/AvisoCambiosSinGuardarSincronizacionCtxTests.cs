using Bunit;
using CaeManager.Web.Components;
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

    /// <summary>Revisión puente: el formulario dentro de una pestaña del panel (ámbito de pestañas y de ficha a la vez) tampoco se desmonta.</summary>
    [Fact]
    public async Task El_filtro_de_la_pagina_con_un_formulario_de_pestana_del_panel_a_medio_editar_no_pregunta()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.NavigateTo($"centros?q=norte&ctx=Centro:{Centro}:informacion");
        var ficha = new AmbitoCambiosSinGuardar();
        var cut = Render<AvisoCambiosSinGuardar>(p => p
            .Add(x => x.HayCambios, () => true)
            .AddCascadingValue(new AmbitoCambiosSinGuardar(ficha))
            .AddCascadingValue(AmbitoCambiosSinGuardar.NombreAmbitoFicha, ficha));

        await cut.InvokeAsync(() => navegacion.NavigateTo($"centros?q=sur&ctx=Centro:{Centro}:informacion", replace: true));

        navegacion.Uri.Should().Contain("q=sur");
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

    /// <summary>
    /// Revisión puente: un filtro de la página sigue en vuelo y el panel cambia de pestaña. Detiene la escritura el
    /// formulario de dentro de la pestaña (X); el de fuera de las pestañas (Y), de la misma ficha y también con cambios,
    /// no corre peligro: no se adhiere a la pregunta, «Salir y descartar» no lo descarta y la escritura que se repite no
    /// le hace preguntar. Y al «Seguir editando» de otra vez, el filtro en vuelo sigue siendo la base de la siguiente.
    /// </summary>
    [Fact]
    public async Task Con_un_filtro_en_vuelo_el_cambio_de_pestana_detenido_solo_descarta_el_formulario_de_la_pestana()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        var navegacion = new NavegacionRetenida($"http://localhost/centros?ctx=Centro:{Centro}:informacion") { ConAvisos = true };
        Services.AddSingleton<NavigationManager>(navegacion);
        var ficha = new AmbitoCambiosSinGuardar();
        var descartesX = 0;
        var descartesY = 0;
        var x = Render<AvisoCambiosSinGuardar>(p => p
            .Add(a => a.HayCambios, () => true)
            .Add(a => a.AlDescartar, () => descartesX++)
            .AddCascadingValue(new AmbitoCambiosSinGuardar(ficha))
            .AddCascadingValue(AmbitoCambiosSinGuardar.NombreAmbitoFicha, ficha));
        var y = Render<AvisoCambiosSinGuardar>(p => p
            .Add(a => a.HayCambios, () => true)
            .Add(a => a.AlDescartar, () => descartesY++)
            .AddCascadingValue(AmbitoCambiosSinGuardar.NombreAmbitoFicha, ficha));
        navegacion.Retener = true;

        await x.InvokeAsync(() => navegacion.ActualizarFiltroEnUrl("q", "norte"));
        var ctx = $"Centro:{Centro}:documentacion";
        await x.InvokeAsync(() => navegacion.ActualizarFiltroEnUrl("ctx", ctx, estadoHistorial: null));

        x.FindAll(".modal-contenido").Should().ContainSingle("el formulario de la pestaña se desmontaría");
        y.FindAll(".modal-contenido").Should().BeEmpty();
        navegacion.Destinos.Should().HaveCount(1, "la escritura de ?ctx= quedó detenida");

        await x.PulsarEnElAvisoAsync("Seguir editando");
        await x.InvokeAsync(() => navegacion.ActualizarFiltroEnUrl("orden", "nombre"));
        var parametros = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(navegacion.Destinos[^1]).Query);
        parametros["q"].ToString().Should().Be("norte", "el filtro en vuelo sigue siendo la base de la cadena");
        parametros["ctx"].ToString().Should().Be($"Centro:{Centro}:informacion", "la pestaña detenida no reaparece");

        await x.InvokeAsync(() => navegacion.ActualizarFiltroEnUrl("ctx", ctx, estadoHistorial: null));
        await x.PulsarEnElAvisoAsync("Salir y descartar");

        descartesX.Should().Be(1);
        descartesY.Should().Be(0, "el formulario de fuera de las pestañas sigue montado");
        y.FindAll(".modal-contenido").Should().BeEmpty("repetir la escritura tampoco le hace preguntar");
        parametros = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(navegacion.Destinos[^1]).Query);
        parametros["ctx"].ToString().Should().Be(ctx);
        parametros["q"].ToString().Should().Be("norte");
        parametros["orden"].ToString().Should().Be("nombre");
    }

    /// <summary>
    /// Revisión puente: un destino detenido se retira de la cadena pero su base se recuerda. Muchas detenciones seguidas
    /// sin ningún acuse no hacen crecer esa memoria más allá del tope de la cadena.
    /// </summary>
    [Fact]
    public async Task Muchas_escrituras_detenidas_sin_acuse_no_crecen_mas_alla_del_tope()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        var navegacion = new NavegacionRetenida($"http://localhost/centros?ctx=Centro:{Centro}:informacion") { ConAvisos = true, Retener = true };
        Services.AddSingleton<NavigationManager>(navegacion);
        var cut = Render<AvisoCambiosSinGuardar>(p => p.Add(a => a.HayCambios, () => true));

        for (var i = 0; i < 40; i++)
        {
            var valor = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await cut.InvokeAsync(() => navegacion.ActualizarFiltroEnUrl("q", valor));
            await cut.InvokeAsync(() => navegacion.ActualizarFiltroEnUrl("pestana", valor, estadoHistorial: null));
            cut.FindAll(".modal-contenido").Should().ContainSingle();
            await cut.PulsarEnElAvisoAsync("Seguir editando");
            BasesRecordadas(navegacion).Should().BeLessThanOrEqualTo(32, $"vuelta {i}");
        }

        var parametros = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(navegacion.Destinos[^1]).Query);
        parametros["q"].ToString().Should().Be("39");
        parametros.Should().NotContainKey("pestana", "ninguna pestaña detenida reaparece");
    }

    /// <summary>Entradas de la memoria privada de bases del ayudante para este NavigationManager (solo para medir el tope).</summary>
    private static int BasesRecordadas(NavigationManager navegacion)
    {
        const System.Reflection.BindingFlags Privado = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
        var tabla = typeof(CaeManager.Web.Components.NavigationManagerExtensions).GetField("Pendientes", Privado)!.GetValue(null)!;
        var argumentos = new object?[] { navegacion, null };
        tabla.GetType().GetMethod("TryGetValue")!.Invoke(tabla, argumentos).Should().Be(true);
        var bases = (System.Collections.ICollection)argumentos[1]!.GetType().GetField("_bases", Privado)!.GetValue(argumentos[1])!;
        return bases.Count;
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
