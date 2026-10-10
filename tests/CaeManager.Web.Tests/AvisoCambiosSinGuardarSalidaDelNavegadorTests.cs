using Bunit;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CaeManager.Web.Tests;

/// <summary>
/// El clic en un enlace interno y «atrás»/«adelante» del navegador no pasan por el <c>NavigationLock</c> (el Router no es
/// interactivo): el navegador pregunta antes a <see cref="SalidaDelNavegador.ConsultarSalida"/>. Aquí se fija la parte de
/// servidor de ese acuerdo, con el punto de consulta que el propio aviso entrega al navegador al montarse; que el navegador
/// pregunte de verdad y deshaga el recorrido lo fija <c>AvisoSalidaDelNavegadorE2ETests</c>, que es quien puede verlo.
/// También la otra mitad del mismo cambio: la escritura de filtros de la propia página no es una salida.
/// </summary>
public class AvisoCambiosSinGuardarSalidaDelNavegadorTests : BunitContext
{
    // El navegador dice el destino en absoluto (enlace.href, location.href); la base de bUnit es http://localhost/.
    private const string Destino = "http://localhost/trabajadores";

    private bool _kitHayCambios;
    private bool _paginaHayCambios = true;
    private int _descartesKit;
    private int _descartesPagina;

    public AvisoCambiosSinGuardarSalidaDelNavegadorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<IComponent> KitYAvisoDePagina() =>
        Render(b =>
        {
            b.OpenComponent<DrawerFormulario>(0);
            b.AddAttribute(1, nameof(DrawerFormulario.Visible), true);
            b.AddAttribute(2, nameof(DrawerFormulario.Titulo), "Nuevo");
            b.AddAttribute(3, nameof(DrawerFormulario.HayCambios), (Func<bool>)(() => _kitHayCambios));
            b.AddAttribute(4, nameof(DrawerFormulario.TextoGuardar), "Guardar");
            b.AddAttribute(5, nameof(DrawerFormulario.TextoCancelar), "Cancelar");
            b.AddAttribute(6, nameof(DrawerFormulario.AlDescartarPorNavegacion), EventCallback.Factory.Create(this, () => _descartesKit++));
            b.CloseComponent();

            b.OpenComponent<AvisoCambiosSinGuardar>(10);
            b.AddAttribute(11, nameof(AvisoCambiosSinGuardar.HayCambios), (Func<bool>)(() => _paginaHayCambios));
            b.AddAttribute(12, nameof(AvisoCambiosSinGuardar.AlDescartar), EventCallback.Factory.Create(this, () => _descartesPagina++));
            b.CloseComponent();
        });

    /// <summary>El punto de consulta que el aviso entregó al navegador (control positivo: se presentó).</summary>
    private SalidaDelNavegador PuntoDeConsulta()
    {
        var presentaciones = JSInterop.Invocations["talvegAvisoSalida.conectar"];
        presentaciones.Should().NotBeEmpty("cada aviso se presenta al navegador tras su primer render");
        return presentaciones.Select(i => i.Arguments[0]).OfType<DotNetObjectReference<SalidaDelNavegador>>().Last().Value;
    }

    private Task<bool> ConsultarAsync(IRenderedComponent<IComponent> cut, bool esRecorrido, string destino = Destino)
    {
        var punto = PuntoDeConsulta();
        return cut.InvokeAsync(() => punto.ConsultarSalida(destino, esRecorrido));
    }

    private int RecorridosReanudados => JSInterop.Invocations["talvegAvisoSalida.reanudar"].Count;

    private static int Preguntas(IRenderedComponent<IComponent> cut) =>
        cut.FindAll(".modal-pie button").Count(b => b.TextContent.Trim() == "Salir y descartar");

    private static Task Pulsar(IRenderedComponent<IComponent> cut, string texto) =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == texto).ClickAsync(new MouseEventArgs());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sin_cambios_la_navegacion_del_navegador_no_se_detiene(bool esRecorrido)
    {
        _paginaHayCambios = false;
        var cut = KitYAvisoDePagina();

        (await ConsultarAsync(cut, esRecorrido)).Should().BeFalse("sin nada que perder el navegador sigue su camino");

        Preguntas(cut).Should().Be(0);
    }

    [Theory]
    [InlineData("https://otro.example/trabajadores")]
    [InlineData("/trabajadores")]
    [InlineData("javascript:alert(1)")]
    public async Task Un_destino_que_no_es_una_url_de_la_aplicacion_no_se_atiende(string destino)
    {
        var cut = KitYAvisoDePagina();

        (await ConsultarAsync(cut, esRecorrido: false, destino)).Should().BeFalse("el servidor no va a navegar a donde diga el navegador");

        Preguntas(cut).Should().Be(0);
    }

    [Fact]
    public async Task El_clic_en_un_enlace_con_cambios_pregunta_y_al_descartar_navega_el_servidor()
    {
        var cut = KitYAvisoDePagina();
        var origen = Navegacion.Uri;

        (await ConsultarAsync(cut, esRecorrido: false)).Should().BeTrue("con cambios el navegador no navega");
        Preguntas(cut).Should().Be(1);
        Navegacion.Uri.Should().Be(origen);

        await Pulsar(cut, "Salir y descartar");

        _descartesPagina.Should().Be(1);
        Navegacion.Uri.Should().EndWith(Destino, "el enlace detenido lo navega el servidor, ya sin preguntar");
        RecorridosReanudados.Should().Be(0, "un enlace no es un recorrido del historial");
        Preguntas(cut).Should().Be(0);
    }

    [Fact]
    public async Task Atras_con_cambios_pregunta_y_al_descartar_pide_al_navegador_repetir_el_recorrido()
    {
        var cut = KitYAvisoDePagina();
        var origen = Navegacion.Uri;

        (await ConsultarAsync(cut, esRecorrido: true)).Should().BeTrue();
        Preguntas(cut).Should().Be(1);

        await Pulsar(cut, "Salir y descartar");

        _descartesPagina.Should().Be(1);
        RecorridosReanudados.Should().Be(1, "el recorrido lo repite el navegador: NavigateTo añadiría una entrada al historial");
        Navegacion.Uri.Should().Be(origen, "el servidor no navega por su cuenta");
        Preguntas(cut).Should().Be(0);

        // Repetir el recorrido no pasa por el NavigationLock: no puede quedar una salida confirmada sin consumir,
        // que dejaría pasar sin preguntar la siguiente navegación del servidor.
        await cut.InvokeAsync(() => Navegacion.NavigateTo(Destino));
        Navegacion.Uri.Should().Be(origen, "el aviso sigue con cambios y la navegación siguiente vuelve a preguntar");
        Preguntas(cut).Should().Be(1);
    }

    [Fact]
    public async Task Atras_con_cambios_y_seguir_editando_no_descarta_ni_repite_el_recorrido()
    {
        var cut = KitYAvisoDePagina();

        (await ConsultarAsync(cut, esRecorrido: true)).Should().BeTrue();
        await Pulsar(cut, "Seguir editando");

        _descartesPagina.Should().Be(0);
        RecorridosReanudados.Should().Be(0);
        Preguntas(cut).Should().Be(0);

        // El recorrido olvidado no contamina la pregunta siguiente: la de un enlace navega desde el servidor.
        (await ConsultarAsync(cut, esRecorrido: false)).Should().BeTrue();
        await Pulsar(cut, "Salir y descartar");
        RecorridosReanudados.Should().Be(0);
        Navegacion.Uri.Should().EndWith(Destino);
    }

    [Fact]
    public async Task Atras_con_cambios_en_el_kit_y_en_la_pagina_pregunta_una_vez_y_descarta_a_los_dos()
    {
        _kitHayCambios = true;
        var cut = KitYAvisoDePagina();

        (await ConsultarAsync(cut, esRecorrido: true)).Should().BeTrue();
        Preguntas(cut).Should().Be(1, "una navegación, una pregunta");

        await Pulsar(cut, "Salir y descartar");

        _descartesKit.Should().Be(1);
        _descartesPagina.Should().Be(1);
        RecorridosReanudados.Should().Be(1, "los dos siguen diciendo que hay cambios y aun así no se vuelve a preguntar a ninguno");
        Preguntas(cut).Should().Be(0);
    }

    [Fact]
    public async Task Con_la_pregunta_abierta_otra_navegacion_del_navegador_tampoco_pasa_y_no_abre_otra()
    {
        var cut = KitYAvisoDePagina();

        (await ConsultarAsync(cut, esRecorrido: true)).Should().BeTrue();
        (await ConsultarAsync(cut, esRecorrido: false, destino: "http://localhost/centros")).Should().BeTrue("sigue pendiente la primera");

        Preguntas(cut).Should().Be(1);
        await Pulsar(cut, "Salir y descartar");
        RecorridosReanudados.Should().Be(1, "la respuesta es para la primera navegación, el recorrido");
    }

    [Fact]
    public async Task Escribir_un_filtro_de_la_propia_pagina_en_la_url_no_pregunta_aunque_haya_cambios()
    {
        var cut = KitYAvisoDePagina();

        await cut.InvokeAsync(() => Navegacion.ActualizarFiltroEnUrl("q", "foto"));

        Preguntas(cut).Should().Be(0, "la página sigue montada con lo escrito: no es una salida");
        Navegacion.Uri.Should().EndWith("?q=foto", "la URL tiene que decir el filtro que la lista ya aplica");
        _descartesPagina.Should().Be(0);

        // Control: otra navegación a la misma página con otra query, que no es la escritura de filtros, sí pregunta.
        await cut.InvokeAsync(() => Navegacion.NavigateTo(Navegacion.GetUriWithQueryParameter("accion", "nuevo")));
        Preguntas(cut).Should().Be(1);
        Navegacion.Uri.Should().EndWith("?q=foto");
    }

    /// <summary>
    /// El script tiene que cargarse ANTES que blazor.web.js: los oyentes de popstate de window corren en el orden en
    /// que se registraron, y los de Blazor avisan al circuito del cambio de URL en cuanto lo ven. Cargado después, una
    /// ficha del Context Workspace se cerraba con lo escrito antes de poder preguntar (medido en navegador; lo observa
    /// de verdad <c>AvisoSalidaDelNavegadorE2ETests</c>, esto solo evita que alguien lo reordene sin enterarse).
    /// </summary>
    [Fact]
    public void El_script_de_salida_del_navegador_se_carga_antes_que_blazor()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer App.razor");
        var app = File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Components", "App.razor"));

        var propio = app.IndexOf("<script src=\"@Assets[\"js/aviso-salida-navegador.js\"]\">", StringComparison.Ordinal);
        var blazor = app.IndexOf("<script src=\"@Assets[\"_framework/blazor.web.js\"]\">", StringComparison.Ordinal);

        propio.Should().BeGreaterThan(-1, "App.razor carga el script");
        blazor.Should().BeGreaterThan(-1, "control positivo: se reconoce la etiqueta de blazor.web.js");
        propio.Should().BeLessThan(blazor);
    }
}
