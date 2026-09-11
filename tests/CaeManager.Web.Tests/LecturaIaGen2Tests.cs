using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Configuracion.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lectura IA (selección de Cliente empresarial) contra su mockup Gen 2
/// («Lectura IA TALVEG.dc.html», pantalla A).
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> qué consulta llega al mediador y cuántas
/// veces; qué se pinta con lo que vuelve (enlaces, filtro en memoria, vacío,
/// sin coincidencias, error y reintento); que un doble clic en Reintentar no
/// lanza dos cargas (mediador controlado por
/// <see cref="TaskCompletionSource{TResult}"/>); el título y la vuelta según
/// esté embebida o no; y que ni la pantalla ni la entrada del hub prometen un
/// umbral que no existe.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> el alcance ni la autorización (de
/// <c>ObtenerClientesParaSelectorQueryHandler</c> y del atributo de rol, que
/// bUnit no aplica); que el Nivel 2 tenga el efecto que la nota describe (de
/// <c>DeteccionTrabajadoresService</c>, en Application); ni el aspecto (CSS).
/// </para>
/// </summary>
public class LecturaIaGen2Tests : BunitContext
{
    public LecturaIaGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly ClienteSelectorDto Refrielectric = new(Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001"), "Refrielectric S.L.");
    private static readonly ClienteSelectorDto MontajesEbro = new(Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002"), "Montajes Ebro S.A.");
    private static readonly ClienteSelectorDto Dexter = new(Guid.Parse("c3c3c3c3-0000-0000-0000-000000000003"), "Dexter Industrial");

    // ---------------------------------------------------------------- dobles

    private sealed class MediadorControlado(Func<object, Task<object?>> responder) : IMediator
    {
        public List<object> Enviados { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return (TResponse)(await responder(request))!;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException("Esta pantalla no envía comandos.");

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Esta pantalla no envía peticiones sin tipo.");

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    /// <summary>
    /// Datos que ve el mediador. La consulta del selector no lleva parámetros;
    /// el doble ordena por razón social, como el handler real. Cualquier otra
    /// petición falla: esta pantalla solo lee.
    /// </summary>
    private sealed class Escenario
    {
        public List<ClienteSelectorDto> Clientes { get; } = [Refrielectric, MontajesEbro, Dexter];

        /// <summary>Si devuelve una tarea, esa petición se resuelve (o falla) cuando el test lo diga.</summary>
        public Func<object, Task<object?>?> Interceptar { get; set; } = _ => null;

        public Task<object?> Responder(object peticion) =>
            Interceptar(peticion) ?? Task.FromResult<object?>(peticion switch
            {
                ObtenerClientesParaSelectorQuery => Clientes.OrderBy(c => c.RazonSocial).ToList(),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
            });
    }

    // ---------------------------------------------------------------- arnés

    private MediadorControlado Registrar(Escenario escenario)
    {
        var mediador = new MediadorControlado(escenario.Responder);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddSingleton<ILogger<SeleccionarClienteLecturaIa>>(_ => NullLogger<SeleccionarClienteLecturaIa>.Instance);
        return mediador;
    }

    private (IRenderedComponent<SeleccionarClienteLecturaIa> Cut, MediadorControlado Mediador) Renderizar(
        Escenario escenario, bool integrada = true)
    {
        var mediador = Registrar(escenario);
        var cut = Render<SeleccionarClienteLecturaIa>(p => p.Add(x => x.IntegradaEnConfiguracion, integrada));
        return (cut, mediador);
    }

    private static IReadOnlyList<IElement> Filas(IRenderedComponent<SeleccionarClienteLecturaIa> cut) =>
        cut.FindAll(".lista-seleccion-cliente a.item-seleccion-cliente");

    private static IReadOnlyList<string> Nombres(IRenderedComponent<SeleccionarClienteLecturaIa> cut) =>
        Filas(cut).Select(a => a.QuerySelector("span")!.TextContent.Trim()).ToList();

    /// <summary>CampoTexto notifica tras su debounce: InputAsync espera a que el valor llegue a la página.</summary>
    private static Task Buscar(IRenderedComponent<SeleccionarClienteLecturaIa> cut, string texto) =>
        cut.Find("input.campo-input").InputAsync(new ChangeEventArgs { Value = texto });

    private static IElement BotonReintentar(IRenderedComponent<SeleccionarClienteLecturaIa> cut) =>
        cut.FindAll(".estado-vacio button").Single(b => b.TextContent.Trim() == "Reintentar");

    private static int Consultas(MediadorControlado mediador) => mediador.Enviados.OfType<ObtenerClientesParaSelectorQuery>().Count();

    // ---------------------------------------------------------------- lista

    [Fact]
    public void Cada_fila_es_un_enlace_a_la_configuracion_de_su_Cliente_empresarial_en_el_orden_de_la_consulta()
    {
        var (cut, mediador) = Renderizar(new Escenario());

        Consultas(mediador).Should().Be(1);
        Nombres(cut).Should().Equal(["Dexter Industrial", "Montajes Ebro S.A.", "Refrielectric S.L."]);
        Filas(cut).Select(a => a.GetAttribute("href")).Should().Equal(
        [
            $"/clientes/{Dexter.Id}/lectura-ia",
            $"/clientes/{MontajesEbro.Id}/lectura-ia",
            $"/clientes/{Refrielectric.Id}/lectura-ia",
        ], "cada fila navega de verdad a la configuración de SU Cliente empresarial, no a la de otro");
    }

    [Fact]
    public async Task El_filtro_es_en_memoria_y_no_distingue_mayusculas_ni_espacios_alrededor()
    {
        var (cut, mediador) = Renderizar(new Escenario());

        await Buscar(cut, "  EBRO ");

        Nombres(cut).Should().Equal(["Montajes Ebro S.A."]);
        Consultas(mediador).Should().Be(1, "filtrar no vuelve al servidor: filtra la lista ya cargada");

        await Buscar(cut, "");

        Nombres(cut).Should().HaveCount(3, "vaciar la búsqueda devuelve la lista entera");
    }

    [Fact]
    public async Task Sin_coincidencias_dice_que_se_busco_y_no_pinta_lista()
    {
        var (cut, _) = Renderizar(new Escenario());

        await Buscar(cut, "zzz");

        cut.FindAll(".lista-seleccion-cliente").Should().BeEmpty();
        cut.Find(".sin-coincidencias-lectura-ia").TextContent.Trim()
            .Should().Be("Ningún Cliente empresarial coincide con «zzz».");
        cut.FindAll("input.campo-input").Should().ContainSingle("el buscador sigue ahí para corregir la búsqueda");
    }

    [Fact]
    public void Sin_ningun_Cliente_empresarial_pinta_el_vacio_y_no_el_buscador()
    {
        var escenario = new Escenario();
        escenario.Clientes.Clear();
        var (cut, _) = Renderizar(escenario);

        cut.Find(".estado-vacio h3").TextContent.Trim().Should().Be("Todavía no hay ningún Cliente empresarial");
        cut.FindAll("input.campo-input").Should().BeEmpty("filtrar una lista vacía no lleva a ninguna parte");
        cut.FindAll(".lista-seleccion-cliente").Should().BeEmpty();
    }

    // ---------------------------------------------------------------- error y reintento

    [Fact]
    public async Task Si_la_carga_falla_ofrece_reintentar_y_el_reintento_pinta_la_lista()
    {
        var escenario = new Escenario();
        var fallos = 1;
        escenario.Interceptar = p => p is ObtenerClientesParaSelectorQuery && fallos-- > 0
            ? Task.FromException<object?>(new InvalidOperationException("caída simulada"))
            : null;
        var (cut, mediador) = Renderizar(escenario);

        cut.Find(".estado-vacio h3").TextContent.Trim().Should().Be("No pudimos cargar los Clientes empresariales");
        Filas(cut).Should().BeEmpty();

        await BotonReintentar(cut).ClickAsync(new MouseEventArgs());

        Consultas(mediador).Should().Be(2);
        cut.FindAll(".estado-vacio").Should().BeEmpty("el reintento salió bien: el error ya no aplica");
        Nombres(cut).Should().HaveCount(3);
    }

    [Fact]
    public async Task Un_doble_clic_en_reintentar_no_lanza_una_segunda_carga()
    {
        var escenario = new Escenario();
        var primera = true;
        var reintento = new TaskCompletionSource<object?>();
        escenario.Interceptar = p =>
        {
            if (p is not ObtenerClientesParaSelectorQuery) return null;
            if (primera)
            {
                primera = false;
                return Task.FromException<object?>(new InvalidOperationException("caída simulada"));
            }
            return reintento.Task;
        };
        var (cut, mediador) = Renderizar(escenario);

        // Sin await: el doble retiene el reintento hasta que el test lo suelte.
        var primerClic = BotonReintentar(cut).ClickAsync(new MouseEventArgs());

        BotonReintentar(cut).HasAttribute("disabled").Should().BeTrue("mientras reintenta, el botón está en carga");
        // Boton deja el @onclick enganchado aunque esté deshabilitado: el
        // segundo clic llega al manejador y es la guarda la que lo para. Sin
        // await: si la guarda faltara, este clic pediría otra carga que el
        // doble también retiene, y esperarlo colgaría el test en vez de
        // dejarlo fallar con su mensaje.
        var segundoClic = BotonReintentar(cut).ClickAsync(new MouseEventArgs());

        Consultas(mediador).Should().Be(2, "la carga inicial y UN reintento; el segundo clic no pide otra");

        await cut.InvokeAsync(() => reintento.SetResult(escenario.Clientes.OrderBy(c => c.RazonSocial).ToList()));
        await primerClic;
        await segundoClic;

        Nombres(cut).Should().HaveCount(3);
    }

    // ---------------------------------------------------------------- cabecera y copy

    [Fact]
    public void Embebida_el_titulo_es_h2_y_no_hay_vuelta_suelta_con_h1_y_vuelta_al_hub()
    {
        var escenario = new Escenario();
        Registrar(escenario);

        var integrada = Render<SeleccionarClienteLecturaIa>(p => p.Add(x => x.IntegradaEnConfiguracion, true));
        integrada.Find("h2").TextContent.Trim().Should().Be("Lectura IA por Cliente empresarial");
        integrada.FindAll("h1").Should().BeEmpty("en el hub el h1 es «Configuración»");
        integrada.FindAll("a.enlace-volver-configuracion").Should().BeEmpty("la subnavegación del hub ya está a la izquierda");

        var suelta = Render<SeleccionarClienteLecturaIa>(p => p.Add(x => x.IntegradaEnConfiguracion, false));
        suelta.Find("h1").TextContent.Trim().Should().Be("Lectura IA por Cliente empresarial");
        suelta.Find("a.enlace-volver-configuracion").GetAttribute("href").Should().Be("/configuracion/ia");
    }

    [Fact]
    public void La_entradilla_no_promete_umbral_y_dice_hasta_donde_llega_el_Nivel_2()
    {
        var (cut, _) = Renderizar(new Escenario());

        var entradilla = cut.Find(".cabecera-pagina-descripcion");
        entradilla.TextContent.Should().NotContainEquivalentOf("umbral", "no existe ningún umbral configurable");
        entradilla.QuerySelector("a")!.GetAttribute("href").Should().Be("/configuracion/tipos",
            "el Nivel 1 se activa en Tipos de documento, dentro del mismo hub");

        cut.Find(".nota-alcance-lectura-ia").TextContent.Should().Contain("solo la aplica la detección de trabajadores",
            "desactivar aquí no detiene toda lectura IA: solo la detección de trabajadores consulta el Nivel 2");
        cut.Markup.Should().NotContainEquivalentOf("valida").And.NotContainEquivalentOf("aprueba");
    }

    [Fact]
    public void El_hub_monta_esta_pantalla_en_su_entrada_y_la_describe_sin_umbral()
    {
        Registrar(new Escenario());

        var hub = Render<Configuracion>(p => p.Add(x => x.EntradaRuta, "ia"));

        var entrada = hub.Find(".entrada-subnav[aria-current=page]");
        entrada.QuerySelector(".nombre-entrada-subnav")!.TextContent.Trim().Should().Be("Lectura IA por Cliente empresarial");
        entrada.QuerySelector(".descripcion-entrada-subnav")!.TextContent.Should().NotContainEquivalentOf("umbral");

        var panel = hub.Find(".panel-configuracion-host");
        panel.QuerySelector("h2")!.TextContent.Trim().Should().Be("Lectura IA por Cliente empresarial");
        panel.QuerySelectorAll("a.item-seleccion-cliente").Should().HaveCount(3);
        hub.FindAll("h1").Select(h => h.TextContent.Trim()).Should().Equal(["Configuración"]);
    }
}
