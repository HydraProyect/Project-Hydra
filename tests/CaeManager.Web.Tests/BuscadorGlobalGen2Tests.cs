using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.BusquedaGlobal.Commands.RegistrarUsoReciente;
using CaeManager.Application.BusquedaGlobal.Queries.BuscarGlobal;
using CaeManager.Application.BusquedaGlobal.Queries.ObtenerRecientes;
using CaeManager.Web.Features.BusquedaGlobal;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// El palette global (Ctrl/Cmd+K, <see cref="BuscadorGlobal"/>) contra su
/// mockup Gen 2, y el contrato de comportamiento que el mockup no dibuja pero
/// el overlay tiene que cumplir igual.
///
/// <para><b>Lo que esto SÍ observa</b>: la estructura Gen 2 del panel
/// (cabecera con lupa y chip «esc», secciones con título, caja de icono,
/// pista «↵ …» solo en la fila activa, pie con ámbito y chuleta); la
/// semántica accesible del listado (<c>role="listbox"</c>/<c>option</c>,
/// <c>aria-selected</c> y <c>aria-activedescendant</c> siguiendo al índice);
/// la terminología canónica del subtítulo; la navegación con ↑↓ y Enter y el
/// cierre con Escape, disparados como eventos reales sobre el input; el salto
/// de grupo por su punto de entrada <c>[JSInvokable]</c>; y las cuatro
/// familias del contrato: concurrencia por generación, ausencia de arrastre
/// entre aperturas, guarda de reentrada y desenlaces honestos.</para>
///
/// <para><b>Lo que NO observa</b>: el CSS aislado (bUnit no lo aplica — las
/// clases aquí son solo selectores); la interceptación real de Ctrl/Cmd+K y
/// de Tab, que vive en <c>wwwroot/js/buscador-global.js</c> sobre
/// <c>document</c> y sobre el <c>&lt;input&gt;</c> y no existe bajo
/// <c>JSRuntimeMode.Loose</c> — estos tests entran por
/// <c>AbrirDesdeJs</c>/<c>SaltarGrupoDesdeJs</c>, que es la lógica C# que ese
/// JS invoca, no el atajo del navegador; el foco real del input (se pide por
/// interop, que aquí es un falso); el alcance de cartera y la autorización,
/// que son de Application y de PostgreSQL.</para>
/// </summary>
public class BuscadorGlobalGen2Tests : BunitContext
{
    public BuscadorGlobalGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid IdCliente = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IdTrabajador = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly ResultadoBusquedaGlobalDto SinNada = new([], [], [], [], [], []);

    /// <summary>El subtítulo "Cliente" es literalmente el que produce BuscarGlobalQueryHandler para esa categoría.</summary>
    private static readonly ResultadoBusquedaGlobalDto UnClienteEmpresarial = new(
        [new ItemBusquedaDto(IdCliente, "Refrielectric S.A.", "Cliente", "/clientes?q=Refrielectric")],
        [], [], [], [], []);

    /// <summary>Para Trabajador el handler pone el DNI en el subtítulo, no el tipo.</summary>
    private static readonly ResultadoBusquedaGlobalDto UnTrabajador = new(
        [], [], [], [],
        [new ItemBusquedaDto(IdTrabajador, "Juan Pérez", "12345678Z", "/trabajadores/22222222-2222-2222-2222-222222222222")],
        []);

    private static readonly ResultadoBusquedaGlobalDto DosEntidades = new(
        [new ItemBusquedaDto(IdCliente, "Refrielectric S.A.", "Cliente", "/clientes?q=Refrielectric")],
        [], [], [],
        [new ItemBusquedaDto(IdTrabajador, "Juan Pérez", "12345678Z", "/trabajadores/22222222-2222-2222-2222-222222222222")],
        []);

    /// <summary>
    /// Mediador escrito a mano (no Moq) que además de responder registra lo
    /// enviado y los tokens recibidos, y permite retener una petición para
    /// resolverla fuera de orden. Deliberadamente <b>ignora</b> el
    /// CancellationToken: así los tests de concurrencia miden la guarda de
    /// generación del componente y no la cancelación — que es una petición,
    /// no una garantía, y un handler real puede no atenderla.
    /// </summary>
    private sealed class MediadorControlado : IMediator
    {
        private readonly List<(Func<object, bool> Criterio, TaskCompletionSource<object> Fuente)> _retenciones = [];

        public List<object> Enviados { get; } = [];
        public List<CancellationToken> TokensRecibidos { get; } = [];
        public ResultadoBusquedaGlobalDto Resultado { get; set; } = SinNada;
        public IReadOnlyList<ItemRecienteDto> Recientes { get; set; } = [];
        public Exception? FalloDeBusqueda { get; set; }

        public TaskCompletionSource<object> Retener(Func<object, bool> criterio)
        {
            var fuente = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _retenciones.Add((criterio, fuente));
            return fuente;
        }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request!);
            TokensRecibidos.Add(cancellationToken);

            var retencion = _retenciones.FirstOrDefault(r => r.Criterio(request!));
            if (retencion.Fuente is not null)
            {
                _retenciones.Remove(retencion);
                return (TResponse)await retencion.Fuente.Task;
            }

            if (request is BuscarGlobalQuery && FalloDeBusqueda is not null)
                throw FalloDeBusqueda;

            return (TResponse)(request switch
            {
                ObtenerRecientesQuery => (object)Recientes,
                BuscarGlobalQuery => Resultado,
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            })!;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Enviados.Add(request!);
            TokensRecibidos.Add(cancellationToken);
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return Task.FromResult<object?>(null);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private IRenderedComponent<BuscadorGlobal> Renderizar(MediadorControlado mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<BusquedaGlobalService>();
        return Render<BuscadorGlobal>();
    }

    private async Task<IRenderedComponent<BuscadorGlobal>> RenderizarYAbrir(MediadorControlado mediador)
    {
        var cut = Renderizar(mediador);
        await cut.InvokeAsync(() => cut.Instance.AbrirDesdeJs());
        return cut;
    }

    private static Teclado Input(IRenderedComponent<BuscadorGlobal> cut) => new(cut);

    /// <summary>
    /// Espera a que el mediador registre una petición. No sirve
    /// <c>WaitForState</c> de bUnit: reevalúa su predicado en cada render del
    /// componente, y aquí lo que cambia es el estado del falso —una llamada
    /// que ocurre después del debounce, sin render de por medio—, así que
    /// expiraba sin llegar a comprobarlo nunca.
    /// </summary>
    private static async Task EsperarA(Func<bool> condicion, string queSeEsperaba)
    {
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (!condicion() && DateTime.UtcNow < limite)
            await Task.Delay(10);

        condicion().Should().BeTrue($"el arnés esperaba {queSeEsperaba} y no llegó a ocurrir");
    }

    /// <summary>Azúcar mínimo para no repetir el selector del input en cada test.</summary>
    private sealed class Teclado(IRenderedComponent<BuscadorGlobal> cut)
    {
        public Task EscribirAsync(string texto) => cut.Find("input.buscador-input").InputAsync(texto);

        public Task TeclaAsync(string tecla) =>
            cut.Find("input.buscador-input").KeyDownAsync(new KeyboardEventArgs { Key = tecla });
    }

    // ---------------------------------------------------------------------
    // Validación del instrumento
    // ---------------------------------------------------------------------

    /// <summary>
    /// Control del arnés antes de afirmar nada sobre teclado: si bUnit no
    /// entregase el KeyDown al <c>@onkeydown</c> del componente, todos los
    /// tests de navegación de abajo serían verdes vacíos. Aquí la tecla tiene
    /// que producir un cambio observable (una fila activa donde no la había).
    /// </summary>
    [Fact]
    public async Task El_arnes_entrega_la_pulsacion_al_componente_control_del_instrumento()
    {
        var mediador = new MediadorControlado { Resultado = UnClienteEmpresarial };
        var cut = await RenderizarYAbrir(mediador);
        await Input(cut).EscribirAsync("refri");

        cut.FindAll("[aria-selected=true]").Should().BeEmpty("antes de pulsar nada no hay fila activa");

        await Input(cut).TeclaAsync("ArrowDown");

        cut.FindAll("[aria-selected=true]").Should().ContainSingle(
            "si esto queda vacío, bUnit no está entregando el KeyDown y ningún test de teclado de esta clase observa lo que dice observar");
    }

    // ---------------------------------------------------------------------
    // Mockup Gen 2
    // ---------------------------------------------------------------------

    [Fact]
    public async Task La_cabecera_lleva_la_lupa_y_el_chip_de_escape()
    {
        var cut = await RenderizarYAbrir(new MediadorControlado());

        var cabecera = cut.Find(".buscador-cabecera");
        cabecera.QuerySelector("svg").Should().NotBeNull("el mockup abre la fila con el icono de búsqueda");
        cabecera.QuerySelector(".buscador-tecla-escape")!.TextContent.Trim().Should().Be("esc");
    }

    [Fact]
    public async Task El_pie_declara_el_ambito_de_busqueda_y_la_chuleta_de_teclas()
    {
        var cut = await RenderizarYAbrir(new MediadorControlado());

        cut.Find(".buscador-ambito").TextContent.Should().Contain("tu cartera");
        cut.Find(".buscador-leyenda").TextContent.Should().Contain("navegar").And.Contain("grupo").And.Contain("cerrar");
    }

    [Fact]
    public async Task Las_entidades_se_agrupan_bajo_un_titulo_de_seccion()
    {
        var mediador = new MediadorControlado { Resultado = UnClienteEmpresarial };
        var cut = await RenderizarYAbrir(mediador);

        await Input(cut).EscribirAsync("refri");

        cut.FindAll(".buscador-grupo-titulo").Select(t => t.TextContent.Trim())
            .Should().Contain("Entidades");
    }

    /// <summary>
    /// El mockup pone el tipo en el subtítulo de cada fila. El contrato de
    /// lenguaje decide cuál: la contraparte de una Relación Empresarial es el
    /// <b>Cliente empresarial</b>, nunca "cliente" a secas — y el literal que
    /// llega del handler es justamente "Cliente".
    /// </summary>
    [Fact]
    public async Task El_subtitulo_de_una_contraparte_dice_Cliente_empresarial()
    {
        var mediador = new MediadorControlado { Resultado = UnClienteEmpresarial };
        var cut = await RenderizarYAbrir(mediador);

        await Input(cut).EscribirAsync("refri");

        cut.Find(".buscador-item-subtitulo").TextContent.Trim().Should().Be("Cliente empresarial");
    }

    /// <summary>Cuando el subtítulo del DTO aporta algo distinto del tipo (el DNI), se concatena en vez de sustituirse.</summary>
    [Fact]
    public async Task El_subtitulo_de_un_trabajador_compone_el_tipo_con_el_dato_del_dto()
    {
        var mediador = new MediadorControlado { Resultado = UnTrabajador };
        var cut = await RenderizarYAbrir(mediador);

        await Input(cut).EscribirAsync("juan");

        cut.Find(".buscador-item-subtitulo").TextContent.Trim().Should().Be("Trabajador · 12345678Z");
    }

    /// <summary>La pista «↵ …» del mockup aparece SOLO en la fila activa, y dice el verbo de esa fila.</summary>
    [Fact]
    public async Task La_pista_de_Enter_solo_aparece_en_la_fila_activa()
    {
        var mediador = new MediadorControlado { Resultado = DosEntidades };
        var cut = await RenderizarYAbrir(mediador);
        await Input(cut).EscribirAsync("refri");

        cut.FindAll(".buscador-item-pista").Should().BeEmpty("sin fila activa no hay pista que dar");

        await Input(cut).TeclaAsync("ArrowDown");

        var pistas = cut.FindAll(".buscador-item-pista");
        pistas.Should().ContainSingle("la pista pertenece a la fila activa, no a todas");
        pistas[0].TextContent.Should().Contain("abrir ficha");
    }

    [Fact]
    public async Task La_pista_de_un_destino_de_navegacion_dice_ir_no_abrir_ficha()
    {
        var mediador = new MediadorControlado { Resultado = SinNada };
        var cut = await RenderizarYAbrir(mediador);

        await Input(cut).EscribirAsync("veh");
        await Input(cut).TeclaAsync("ArrowDown");

        cut.Find(".buscador-item-pista").TextContent.Should().Contain("ir").And.NotContain("abrir ficha");
    }

    // ---------------------------------------------------------------------
    // Accesibilidad
    // ---------------------------------------------------------------------

    [Fact]
    public async Task El_listado_es_un_listbox_de_opciones_anunciables()
    {
        var mediador = new MediadorControlado { Resultado = DosEntidades };
        var cut = await RenderizarYAbrir(mediador);

        await Input(cut).EscribirAsync("refri");

        cut.Find("[role=listbox]").Should().NotBeNull();
        cut.FindAll("[role=option]").Should().HaveCountGreaterThanOrEqualTo(2);
        cut.FindAll("[role=option]").Should().OnlyContain(o => o.HasAttribute("aria-selected"));
    }

    /// <summary>
    /// El elemento activo tiene que ser <i>anunciable</i>, no solo estar
    /// pintado de otro color: el input apunta a su id y solo esa fila lleva
    /// aria-selected=true.
    /// </summary>
    [Fact]
    public async Task El_input_apunta_a_la_fila_activa_con_aria_activedescendant()
    {
        var mediador = new MediadorControlado { Resultado = DosEntidades };
        var cut = await RenderizarYAbrir(mediador);
        await Input(cut).EscribirAsync("refri");

        cut.Find("input.buscador-input").HasAttribute("aria-activedescendant")
            .Should().BeFalse("sin fila activa el input no puede apuntar a ninguna");

        await Input(cut).TeclaAsync("ArrowDown");
        await Input(cut).TeclaAsync("ArrowDown");

        var activa = cut.FindAll("[aria-selected=true]").Should().ContainSingle().Subject;
        cut.Find("input.buscador-input").GetAttribute("aria-activedescendant")
            .Should().Be(activa.Id, "un lector de pantalla anuncia la fila a la que apunta el input, no la que tiene otro fondo");
    }

    // ---------------------------------------------------------------------
    // Comportamiento de teclado conservado
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Enter_abre_la_fila_activa()
    {
        var mediador = new MediadorControlado { Resultado = UnClienteEmpresarial };
        var cut = await RenderizarYAbrir(mediador);
        await Input(cut).EscribirAsync("refri");

        await Input(cut).TeclaAsync("ArrowDown");
        await Input(cut).TeclaAsync("Enter");

        Services.GetRequiredService<NavigationManager>().Uri
            .Should().EndWith("/clientes?q=Refrielectric");
    }

    [Fact]
    public async Task Escape_cierra_el_palette()
    {
        var cut = await RenderizarYAbrir(new MediadorControlado());

        cut.FindAll(".buscador-panel").Should().ContainSingle();

        await Input(cut).TeclaAsync("Escape");

        cut.FindAll(".buscador-panel").Should().BeEmpty();
    }

    [Fact]
    public async Task ArrowUp_no_se_sale_por_arriba_de_la_lista()
    {
        var mediador = new MediadorControlado { Resultado = DosEntidades };
        var cut = await RenderizarYAbrir(mediador);
        await Input(cut).EscribirAsync("refri");

        await Input(cut).TeclaAsync("ArrowDown");
        await Input(cut).TeclaAsync("ArrowUp");
        await Input(cut).TeclaAsync("ArrowUp");

        cut.FindAll("[aria-selected=true]").Should().ContainSingle()
            .Which.Id.Should().Be("buscador-item-0");
    }

    /// <summary>
    /// Tab salta de grupo, no de fila. Entra por el mismo método
    /// <c>[JSInvokable]</c> que invoca <c>buscador-global.js</c>: la
    /// interceptación del Tab en el navegador no existe en bUnit.
    /// </summary>
    [Fact]
    public async Task Tab_salta_al_primer_elemento_del_grupo_siguiente()
    {
        var mediador = new MediadorControlado { Resultado = UnClienteEmpresarial };
        var cut = await RenderizarYAbrir(mediador);
        await Input(cut).EscribirAsync("client");

        var titulos = cut.FindAll(".buscador-grupo-titulo").Select(t => t.TextContent.Trim()).ToList();
        titulos.Should().HaveCountGreaterThan(1, "este test necesita al menos dos grupos para que Tab tenga a dónde saltar");

        await cut.InvokeAsync(() => cut.Instance.SaltarGrupoDesdeJs(retroceder: false));
        var primerGrupo = cut.FindAll("[aria-selected=true]").Should().ContainSingle().Subject.Id;

        await cut.InvokeAsync(() => cut.Instance.SaltarGrupoDesdeJs(retroceder: false));
        var segundoGrupo = cut.FindAll("[aria-selected=true]").Should().ContainSingle().Subject.Id;

        segundoGrupo.Should().NotBe(primerGrupo, "Tab tiene que moverse a otro grupo, no quedarse donde estaba");
    }

    // ---------------------------------------------------------------------
    // Contrato 1 — concurrencia
    // ---------------------------------------------------------------------

    /// <summary>
    /// Una respuesta lenta de una búsqueda ya superada no puede pintarse
    /// sobre la vigente. El mediador falso ignora el token a propósito: sin
    /// la comprobación de generación posterior al await, el resultado viejo
    /// llegaría igual y sustituiría al nuevo.
    /// </summary>
    [Fact]
    public async Task Una_respuesta_lenta_superada_no_se_pinta_sobre_la_vigente()
    {
        var mediador = new MediadorControlado { Resultado = UnClienteEmpresarial };
        var cut = await RenderizarYAbrir(mediador);

        var retenida = mediador.Retener(r => r is BuscarGlobalQuery q && q.Termino == "lenta");

        var primera = Input(cut).EscribirAsync("lenta");
        await EsperarA(() => mediador.Enviados.OfType<BuscarGlobalQuery>().Any(q => q.Termino == "lenta"), "que la búsqueda de «lenta» llegase al mediador");

        mediador.Resultado = UnTrabajador;
        await Input(cut).EscribirAsync("juan");

        cut.Find(".buscador-item-titulo").TextContent.Should().Contain("Juan Pérez");

        // Llega ahora la respuesta de la búsqueda superada.
        await cut.InvokeAsync(() => retenida.TrySetResult(UnClienteEmpresarial));
        await primera;

        cut.FindAll(".buscador-item-titulo").Select(t => t.TextContent).Should()
            .NotContain(t => t.Contains("Refrielectric", StringComparison.Ordinal),
                "la respuesta de «lenta» pertenece a una búsqueda superada y no puede sustituir a la de «juan»");
        cut.Find(".buscador-item-titulo").TextContent.Should().Contain("Juan Pérez");
    }

    /// <summary>
    /// El indicador de "operación en curso" solo lo apaga la operación
    /// vigente: si lo apagase cualquiera que termine, el final de una
    /// búsqueda superada dejaría a la que sigue en vuelo sin su "Buscando…".
    /// </summary>
    [Fact]
    public async Task El_final_de_una_busqueda_superada_no_apaga_el_indicador_de_la_vigente()
    {
        var mediador = new MediadorControlado { Resultado = UnClienteEmpresarial };
        var cut = await RenderizarYAbrir(mediador);

        var primeraRetenida = mediador.Retener(r => r is BuscarGlobalQuery q && q.Termino == "lenta");
        var segundaRetenida = mediador.Retener(r => r is BuscarGlobalQuery q && q.Termino == "otra");

        var primera = Input(cut).EscribirAsync("lenta");
        await EsperarA(() => mediador.Enviados.OfType<BuscarGlobalQuery>().Any(q => q.Termino == "lenta"), "que la búsqueda de «lenta» llegase al mediador");

        var segunda = Input(cut).EscribirAsync("otra");
        await EsperarA(() => mediador.Enviados.OfType<BuscarGlobalQuery>().Any(q => q.Termino == "otra"), "que la búsqueda de «otra» llegase al mediador");

        // Termina la superada, con la vigente todavía en vuelo.
        await cut.InvokeAsync(() => primeraRetenida.TrySetResult(UnClienteEmpresarial));
        await primera;

        cut.Markup.Should().Contain("Buscando…",
            "la búsqueda de «otra» sigue en vuelo: el final de «lenta» no puede apagar su indicador");

        await cut.InvokeAsync(() => segundaRetenida.TrySetResult(UnTrabajador));
        await segunda;

        cut.Markup.Should().NotContain("Buscando…");
    }

    /// <summary>El token del ciclo de vida viaja en TODAS las llamadas al mediador, no solo en la búsqueda.</summary>
    [Fact]
    public async Task El_token_del_ciclo_de_vida_viaja_en_la_busqueda_y_en_los_recientes()
    {
        var mediador = new MediadorControlado { Resultado = UnClienteEmpresarial };
        var cut = await RenderizarYAbrir(mediador);
        await Input(cut).EscribirAsync("refri");

        mediador.Enviados.Should().Contain(e => e is ObtenerRecientesQuery).And.Contain(e => e is BuscarGlobalQuery);
        mediador.TokensRecibidos.Should().OnlyContain(t => t.CanBeCanceled,
            "un CancellationToken.None significa que esa llamada no se cancela nunca, ni siquiera al destruirse el componente");
    }

    /// <summary>
    /// Al destruirse el componente, el token del ciclo de vida queda
    /// cancelado — y el Dispose no lanza ni deja escapar nada.
    /// </summary>
    [Fact]
    public async Task Al_destruirse_el_componente_se_cancela_el_token_que_repartio()
    {
        var mediador = new MediadorControlado { Resultado = UnClienteEmpresarial };
        var cut = await RenderizarYAbrir(mediador);
        await Input(cut).EscribirAsync("refri");

        var tokens = mediador.TokensRecibidos.Where(t => t.CanBeCanceled).ToList();
        tokens.Should().NotBeEmpty();
        tokens.Should().OnlyContain(t => !t.IsCancellationRequested);

        await cut.Instance.DisposeAsync();

        tokens.Should().OnlyContain(t => t.IsCancellationRequested);
    }

    /// <summary>
    /// Escribir después de que el componente se haya destruido no puede
    /// lanzar <see cref="ObjectDisposedException"/>: es el defecto concreto
    /// de leer el token del CTS de ciclo de vida <i>después</i> de un await,
    /// cuando un Dispose intermedio ya lo desechó y no hay nadie para
    /// recogerlo.
    /// </summary>
    [Fact]
    public async Task Escribir_despues_del_Dispose_no_lanza_ObjectDisposedException()
    {
        var mediador = new MediadorControlado { Resultado = UnClienteEmpresarial };
        var cut = await RenderizarYAbrir(mediador);

        await cut.Instance.DisposeAsync();

        var escribirTrasDispose = async () =>
            await cut.InvokeAsync(() => cut.Instance.AbrirDesdeJs());

        await escribirTrasDispose.Should().NotThrowAsync();
    }

    // ---------------------------------------------------------------------
    // Contrato 2 — nada preparado para una entidad se ejecuta sobre otra
    // ---------------------------------------------------------------------

    /// <summary>
    /// La apertura tiene que limpiar la fila activa <b>ella misma</b>, y este
    /// test tuvo que corregirse dos veces para llegar a observarlo:
    ///
    /// <list type="number">
    /// <item>Cerraba con Escape antes de reabrir — y <c>Cerrar()</c> también
    /// pone el índice a -1, así que tapaba a la limpieza de
    /// <c>AbrirAsync</c>.</item>
    /// <item>Reabría sin recientes — y el estado inicial se quedaba entonces
    /// sin ninguna fila, con lo que "no hay fila activa" se cumplía de forma
    /// trivial aunque el índice se hubiese arrastrado intacto.</item>
    /// </list>
    ///
    /// De ahí las dos piezas de esta versión: se reabre con Ctrl+K estando ya
    /// abierto (<c>AbrirDesdeJs</c> sin pasar por <c>Cerrar</c>), que es el
    /// único camino donde esa guarda puede salvar el caso, y con recientes
    /// cargados, para que haya filas donde un arrastre sería visible.
    /// </summary>
    [Fact]
    public async Task Reabrir_el_palette_no_arrastra_la_fila_que_quedo_activa()
    {
        var mediador = new MediadorControlado
        {
            Resultado = UnClienteEmpresarial,
            Recientes =
            [
                new ItemRecienteDto("Trabajador", IdTrabajador, "Juan Pérez", "12345678Z", "/trabajadores/22222222-2222-2222-2222-222222222222"),
                new ItemRecienteDto("Centro", null, "Centro Norte", "Centro", "/centros/33333333-3333-3333-3333-333333333333")
            ]
        };

        var cut = await RenderizarYAbrir(mediador);
        await Input(cut).EscribirAsync("refri");
        await Input(cut).TeclaAsync("ArrowDown");

        cut.FindAll("[aria-selected=true]").Should().ContainSingle();

        await cut.InvokeAsync(() => cut.Instance.AbrirDesdeJs());

        cut.FindAll("a.buscador-item").Should().NotBeEmpty(
            "control del instrumento: sin filas en el estado inicial, la aserción de abajo se cumpliría sola");
        cut.FindAll("[aria-selected=true]").Should().BeEmpty(
            "al reabrir, un Enter inmediato no puede ejecutarse sobre lo que quedó seleccionado en la apertura anterior");
    }

    /// <summary>
    /// Los "Recientes" que llegan tarde pertenecen a una apertura que ya se
    /// cerró: si se pintasen, el palette recién abierto mostraría —y
    /// ejecutaría con Enter— las filas de un contexto que ya no existe.
    /// </summary>
    [Fact]
    public async Task Los_recientes_de_una_apertura_superada_no_se_pintan_en_la_siguiente()
    {
        var mediador = new MediadorControlado();
        var viejos = new List<ItemRecienteDto>
        {
            new("Cliente", IdCliente, "Refrielectric S.A.", "Cliente", "/clientes?q=Refrielectric")
        };
        var nuevos = new List<ItemRecienteDto>
        {
            new("Trabajador", IdTrabajador, "Juan Pérez", "12345678Z", "/trabajadores/22222222-2222-2222-2222-222222222222")
        };

        var retenida = mediador.Retener(r => r is ObtenerRecientesQuery);

        var cut = Renderizar(mediador);
        var primeraApertura = cut.InvokeAsync(() => cut.Instance.AbrirDesdeJs());
        await EsperarA(() => mediador.Enviados.OfType<ObtenerRecientesQuery>().Any(), "que la carga de recientes llegase al mediador");

        // Se cierra y se vuelve a abrir; esta vez los recientes responden al momento.
        await cut.InvokeAsync(() => cut.Instance.SaltarGrupoDesdeJs(retroceder: false));
        await Input(cut).TeclaAsync("Escape");
        mediador.Recientes = nuevos;
        await cut.InvokeAsync(() => cut.Instance.AbrirDesdeJs());

        // Llegan ahora los de la apertura cerrada.
        await cut.InvokeAsync(() => retenida.TrySetResult(viejos));
        await primeraApertura;

        cut.Markup.Should().NotContain("Refrielectric",
            "esos recientes se pidieron para una apertura que ya se cerró");
        cut.Markup.Should().Contain("Juan Pérez");
    }

    // ---------------------------------------------------------------------
    // Contrato 3 — reentrada
    // ---------------------------------------------------------------------

    /// <summary>
    /// La guarda de reentrada de <c>Seleccionar</c> se reinicia al reabrir el
    /// palette, que es el cambio de contexto. Importa porque una guarda que
    /// no se reinicie no falla de forma ruidosa: deja el palette vivo pero
    /// inerte — se abre, busca y resalta, y ningún Enter ni ningún clic
    /// vuelve a navegar en lo que dure el circuito.
    ///
    /// <para><b>Hueco declarado</b>: lo que este test NO demuestra es el otro
    /// lado de la guarda, que un segundo evento ya en vuelo quede absorbido.
    /// No es observable con bUnit: el renderer procesa el re-render dentro
    /// del propio disparo del primer clic y rechaza el segundo con
    /// <c>UnknownEventHandlerIdException</c> ("no event handler with ID …"),
    /// así que la guarda ni siquiera llega a ejercitarse. Se intentó por dos
    /// vías —nodo crudo de AngleSharp y dos disparos dentro del mismo
    /// <c>InvokeAsync</c>— y las dos mueren en ese mismo punto. Queda para un
    /// E2E con Playwright, donde el doble clic real sí sale del
    /// navegador.</para>
    /// </summary>
    [Fact]
    public async Task La_guarda_de_reentrada_se_reinicia_al_reabrir_el_palette()
    {
        var mediador = new MediadorControlado { Resultado = UnClienteEmpresarial };
        var cut = await RenderizarYAbrir(mediador);
        await Input(cut).EscribirAsync("refri");

        await cut.InvokeAsync(() => cut.Find("a.buscador-item").Click());

        mediador.Enviados.OfType<RegistrarUsoRecienteCommand>().Should().ContainSingle();
        cut.FindAll(".buscador-panel").Should().BeEmpty("seleccionar cierra el palette");

        await cut.InvokeAsync(() => cut.Instance.AbrirDesdeJs());
        await Input(cut).EscribirAsync("refri");
        await cut.InvokeAsync(() => cut.Find("a.buscador-item").Click());

        mediador.Enviados.OfType<RegistrarUsoRecienteCommand>().Should().HaveCount(2,
            "si la guarda no se reiniciase al abrir, el palette quedaría abierto pero inerte tras la primera selección");
    }

    // ---------------------------------------------------------------------
    // Contrato 4 — desenlaces honestos
    // ---------------------------------------------------------------------

    /// <summary>
    /// Un fallo de la búsqueda no puede acabar mostrándose como un vacío ni
    /// como "sin resultados": antes dejaba el resultado en null con el
    /// indicador ya apagado, y el panel no pintaba absolutamente nada.
    /// </summary>
    [Fact]
    public async Task Un_fallo_de_la_busqueda_se_dice_y_no_se_disfraza_de_vacio()
    {
        var mediador = new MediadorControlado { FalloDeBusqueda = new InvalidOperationException("la base no responde") };
        var cut = await RenderizarYAbrir(mediador);

        await Input(cut).EscribirAsync("refri");

        cut.FindAll(".buscador-mensaje-error").Should().ContainSingle();
        cut.Markup.Should().NotContain("Sin resultados",
            "un fallo no es la misma noticia que «no hay nada»");
    }

    /// <summary>"Sin resultados" nombra lo que se escribió: no es lo mismo que decir que no hay nada en la cartera.</summary>
    [Fact]
    public async Task Sin_resultados_nombra_el_termino_escrito_y_el_ambito()
    {
        var mediador = new MediadorControlado { Resultado = SinNada };
        var cut = await RenderizarYAbrir(mediador);

        await Input(cut).EscribirAsync("zzzqqq");

        var mensaje = cut.Find(".buscador-mensaje").TextContent;
        mensaje.Should().Contain("zzzqqq").And.Contain("cartera");
    }

    /// <summary>Un fallo de "Recientes" no puede tumbar el palette: sigue abierto y usable.</summary>
    [Fact]
    public async Task Un_fallo_al_cargar_recientes_deja_el_palette_abierto_y_usable()
    {
        var mediador = new MediadorControlado { Resultado = UnClienteEmpresarial };
        var retenida = mediador.Retener(r => r is ObtenerRecientesQuery);

        var cut = Renderizar(mediador);
        var apertura = cut.InvokeAsync(() => cut.Instance.AbrirDesdeJs());
        await EsperarA(() => mediador.Enviados.OfType<ObtenerRecientesQuery>().Any(), "que la carga de recientes llegase al mediador");

        await cut.InvokeAsync(() => retenida.TrySetException(new InvalidOperationException("sin historial")));
        await apertura;

        cut.FindAll(".buscador-panel").Should().ContainSingle("el palette no depende de los recientes para funcionar");

        await Input(cut).EscribirAsync("refri");
        cut.FindAll("a.buscador-item").Should().NotBeEmpty();
    }
}
