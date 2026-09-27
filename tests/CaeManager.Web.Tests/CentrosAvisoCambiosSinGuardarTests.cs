using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b (lote A): salir de /centros con el alta de Centro a medias pregunta antes. Los
/// modales de crear Cliente o Empresa que se abren encima desde el selector no montan su
/// propio aviso: suman su estado al de la página, y con cambios en los dos se pregunta una
/// sola vez. Lo que trae la URL o el selector no es un cambio, ni lo es lo que «Añadir otro
/// centro» deja puesto tras guardar.
/// </summary>
public class CentrosAvisoCambiosSinGuardarTests : BunitContext
{
    private static readonly Guid ClienteId = Guid.NewGuid();
    private static readonly Guid EmpresaId = Guid.NewGuid();

    public CentrosAvisoCambiosSinGuardarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
    }

    private sealed class MediatorFalso : IMediator
    {
        public List<object> Enviadas { get; } = [];

        /// <summary>
        /// Si está puesta, la segunda carga de Empresas del selector (la acotada al Cliente que
        /// trae la URL; la primera es el catálogo completo al abrir) espera a que el test la abra.
        /// </summary>
        public TaskCompletionSource? PuertaSegundaCargaEmpresas { get; set; }

        private int _cargasEmpresas;

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (request is ObtenerEmpresasParaSelectorQuery && ++_cargasEmpresas == 2 && PuertaSegundaCargaEmpresas is { } puerta)
                await puerta.Task;

            return (TResponse)(object)(request switch
            {
                ObtenerCentrosQuery q => new ResultadoPaginado<CentroListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerProximaVisitaPorCentroQuery => (IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>>)new Dictionary<Guid, IReadOnlyList<VisitaResumenDto>>(),
                ObtenerAlcanceCeroQuery => false,
                ObtenerCandidatosIncorporacionCarteraQuery => Result.Exito<IReadOnlyList<CandidatoIncorporacionCarteraDto>>([]),
                ObtenerClientesParaSelectorQuery => (IReadOnlyList<ClienteSelectorDto>)[new ClienteSelectorDto(ClienteId, "Refrielectric S.A.")],
                ObtenerEmpresasParaSelectorQuery => (IReadOnlyList<EmpresaSelectorDto>)[new EmpresaSelectorDto(EmpresaId, "Montajes Ebro S.L.")],
                CrearCentroCommand => Result.Exito(Guid.NewGuid()),
                CrearClienteCommand => Result.Exito(Guid.NewGuid()),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            });
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private MediatorFalso _mediador = new();

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<Centros> Renderizar(string ruta = "centros")
    {
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddLocalization();
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearCentroCommand>>(_ => new InlineValidator<CrearCentroCommand>());

        Navegacion.NavigateTo(ruta);
        return Render<Centros>();
    }

    private static async Task<IRenderedComponent<Centros>> AbrirAltaAsync(IRenderedComponent<Centros> cut)
    {
        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "+ Nuevo centro"));
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "+ Nuevo centro").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
        return cut;
    }

    private static IElement Control(IRenderedComponent<Centros> cut, string etiqueta) =>
        cut.Find("#" + cut.FindAll("label").Single(l => l.TextContent.Trim() == etiqueta).GetAttribute("for"));

    private static Task EscribirAsync(IRenderedComponent<Centros> cut, string etiqueta, string valor) =>
        Control(cut, etiqueta).InputAsync(new ChangeEventArgs { Value = valor });

    /// <summary>Escribe en el selector de Cliente algo que no existe y pulsa su «+ Crear «…»».</summary>
    private static async Task CrearClienteDesdeElSelectorAsync(IRenderedComponent<Centros> cut, string texto)
    {
        await cut.Find("input[placeholder='Busca o crea un cliente…']").InputAsync(new ChangeEventArgs { Value = texto });
        await cut.Find("li.selector-entidad-opcion-crear").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "Nuevo cliente"));
    }

    private static async Task ElegirEnElSelectorAsync(IRenderedComponent<Centros> cut, string placeholder, string opcion)
    {
        await cut.Find($"input[placeholder='{placeholder}']").InputAsync(new ChangeEventArgs { Value = opcion[..4] });
        await cut.FindAll("li.selector-entidad-opcion").Single(li => li.TextContent.Trim() == opcion).ClickAsync(new MouseEventArgs());
    }

    /// <summary>
    /// El aviso del navegador al recargar o cerrar la pestaña no pasa por HayCambios al
    /// navegar: es el ConfirmExternalNavigation del último render de la página.
    /// </summary>
    private static bool ElNavegadorAvisaAlRecargar(IRenderedComponent<Centros> cut) =>
        cut.FindComponents<NavigationLock>().Any(n => n.Instance.ConfirmExternalNavigation);

    private static int PreguntasDeSalida(IRenderedComponent<Centros> cut) =>
        cut.FindAll(".modal-pie button").Count(b => b.TextContent.Trim() == "Salir y descartar");

    [Fact]
    public async Task Salir_con_el_alta_de_centro_a_medias_pregunta()
    {
        var cut = await AbrirAltaAsync(Renderizar());
        await EscribirAsync(cut, "Nombre", "Planta Zaragoza");

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Abrir_el_alta_sin_tocar_nada_no_pregunta()
    {
        var cut = await AbrirAltaAsync(Renderizar());

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "abrir el formulario no es escribir en él");
    }

    [Fact]
    public async Task Lo_que_trae_la_URL_no_es_un_cambio()
    {
        var cut = Renderizar($"centros?accion=crear&nombre=Planta%20Norte&clienteId={ClienteId}&empresaId={EmpresaId}");
        cut.WaitForAssertion(() => cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "Planta Norte",
            "el test necesita que el nombre llegue preseleccionado por la URL"));
        cut.Markup.Should().Contain("Montajes Ebro S.L.", "y la Empresa, fijada por la cadena");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "lo prefijado por el encadenado no es un cambio de quien edita");
    }

    [Fact]
    public async Task Anadir_otro_centro_tras_guardar_no_deja_nada_pendiente()
    {
        // Cliente y Empresa elegidos a mano (no traídos por la URL): quedan puestos para el
        // siguiente centro, y la instantánea tiene que tomarse otra vez con ellos.
        var cut = await AbrirAltaAsync(Renderizar());
        await ElegirEnElSelectorAsync(cut, "Busca o crea un cliente…", "Refrielectric S.A.");
        await ElegirEnElSelectorAsync(cut, "Busca o crea una empresa…", "Montajes Ebro S.L.");
        await EscribirAsync(cut, "Nombre", "Planta Zaragoza");

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Añadir otro centro").ClickAsync(new MouseEventArgs());

        _mediador.Enviadas.OfType<CrearCentroCommand>().Should().ContainSingle("barrera: el centro se guardó");
        cut.FindAll(".drawer-panel").Should().NotBeEmpty("«Añadir otro centro» deja el drawer abierto");
        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion,
            "Cliente y Empresa mantenidos para el siguiente centro no son un cambio sin guardar");
    }

    [Fact]
    public async Task El_nombre_traido_del_selector_al_modal_de_crear_Cliente_no_es_un_cambio()
    {
        var cut = await AbrirAltaAsync(Renderizar());
        await CrearClienteDesdeElSelectorAsync(cut, "Hierros Aragón");
        cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "Hierros Aragón",
            "el test necesita que la razón social llegue prellenada desde el selector");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "ni el drawer ni el modal tienen nada escrito por quien edita");
    }

    [Fact]
    public async Task Lo_escrito_en_el_modal_de_crear_Cliente_pregunta_al_salir_y_al_cerrarlo_con_la_X()
    {
        var cut = await AbrirAltaAsync(Renderizar());
        await CrearClienteDesdeElSelectorAsync(cut, "Hierros Aragón");
        await EscribirAsync(cut, "Identificación fiscal", "B50123456");

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
        await cut.PulsarEnElAvisoAsync("Seguir editando");

        var modalCliente = cut.FindAll(".modal-contenido").Single(m => m.QuerySelector("h2")?.TextContent.Trim() == "Nuevo cliente");
        await modalCliente.QuerySelector("button.modal-cerrar")!.ClickAsync(new MouseEventArgs());
        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?",
            "la X del modal con algo escrito pregunta antes de tirarlo");
    }

    [Fact]
    public async Task Con_cambios_en_el_drawer_y_en_el_modal_de_encima_se_pregunta_una_sola_vez()
    {
        var cut = await AbrirAltaAsync(Renderizar());
        await EscribirAsync(cut, "Nombre", "Planta Zaragoza");
        await CrearClienteDesdeElSelectorAsync(cut, "Hierros Aragón");
        await EscribirAsync(cut, "Identificación fiscal", "B50123456");

        await cut.InvokeAsync(() => Navegacion.NavigateTo(AvisoCambiosSinGuardarPrueba.DestinoFuera));

        PreguntasDeSalida(cut).Should().Be(1, "dos avisos montados harían dos preguntas seguidas");
        await cut.PulsarEnElAvisoAsync("Salir y descartar");
        Navegacion.Uri.Should().EndWith(AvisoCambiosSinGuardarPrueba.DestinoFuera);
    }

    [Fact]
    public async Task Salir_descartando_cierra_el_modal_y_al_volver_a_abrirlo_parte_de_cero()
    {
        var cut = await AbrirAltaAsync(Renderizar());
        await CrearClienteDesdeElSelectorAsync(cut, "Hierros Aragón");
        await EscribirAsync(cut, "Identificación fiscal", "B50123456");

        // Una salida a la propia página: descartar cierra el drawer y el modal sin desmontar la página.
        await cut.InvokeAsync(() => Navegacion.NavigateTo("centros?q=zaragoza"));
        await cut.PulsarEnElAvisoAsync("Salir y descartar");
        cut.FindAll("h2").Should().NotContain(h => h.TextContent.Trim() == "Nuevo cliente");

        await AbrirAltaAsync(cut);
        await CrearClienteDesdeElSelectorAsync(cut, "Aceros Ebro");
        cut.FindComponents<CampoTexto>().Should().NotContain(c => c.Instance.Valor == "B50123456",
            "lo descartado no reaparece al abrir otra vez el modal");
        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "el modal vuelve a abrirse limpio");
    }

    [Fact]
    public async Task Lo_escrito_en_el_modal_de_crear_Empresa_pregunta_una_vez_y_lo_traido_del_selector_no()
    {
        var cut = await AbrirAltaAsync(Renderizar());
        await ElegirEnElSelectorAsync(cut, "Busca o crea un cliente…", "Refrielectric S.A.");
        await cut.Find("input[placeholder='Busca o crea una empresa…']").InputAsync(new ChangeEventArgs { Value = "Aceros Ebro" });
        await cut.Find("li.selector-entidad-opcion-crear").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "Nueva empresa"));
        var modalEmpresa = cut.FindAll(".modal-contenido").Single(m => m.QuerySelector("h2")?.TextContent.Trim() == "Nueva empresa");

        await modalEmpresa.QuerySelector("button.modal-cerrar")!.ClickAsync(new MouseEventArgs());
        cut.FindAll("h2").Should().NotContain(h => h.TextContent.Trim() == "¿Descartar cambios?",
            "la razón social traída del selector no es un cambio: la X cierra sin preguntar");

        await cut.Find("input[placeholder='Busca o crea una empresa…']").InputAsync(new ChangeEventArgs { Value = "Aceros Ebro" });
        await cut.Find("li.selector-entidad-opcion-crear").ClickAsync(new MouseEventArgs());
        await EscribirAsync(cut, "Identificación fiscal", "B50999999");
        await cut.InvokeAsync(() => Navegacion.NavigateTo(AvisoCambiosSinGuardarPrueba.DestinoFuera));

        PreguntasDeSalida(cut).Should().Be(1, "el Cliente elegido en el drawer y el CIF del modal se preguntan juntos, una vez");
        await cut.PulsarEnElAvisoAsync("Salir y descartar");
        Navegacion.Uri.Should().EndWith(AvisoCambiosSinGuardarPrueba.DestinoFuera,
            "descartar una vez basta: un segundo aviso volvería a detener la salida con otra pregunta");
    }

    [Fact]
    public async Task Escribir_solo_en_el_modal_de_crear_Cliente_activa_el_aviso_del_navegador()
    {
        var cut = await AbrirAltaAsync(Renderizar());
        await CrearClienteDesdeElSelectorAsync(cut, "Hierros Aragón");
        ElNavegadorAvisaAlRecargar(cut).Should().BeFalse("barrera: sin nada escrito, recargar no avisa");

        await EscribirAsync(cut, "Identificación fiscal", "B50123456");

        ElNavegadorAvisaAlRecargar(cut).Should().BeTrue(
            "teclear en el modal tiene que repintar la página: si no, recargar o cerrar la pestaña pierde lo escrito sin aviso");
    }

    [Fact]
    public async Task Escribir_solo_en_el_modal_de_crear_Empresa_activa_el_aviso_del_navegador()
    {
        var cut = Renderizar($"centros?accion=crear&clienteId={ClienteId}");
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
        await cut.Find("input[placeholder='Busca o crea una empresa…']").InputAsync(new ChangeEventArgs { Value = "Aceros Ebro" });
        await cut.Find("li.selector-entidad-opcion-crear").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "Nueva empresa"));
        ElNavegadorAvisaAlRecargar(cut).Should().BeFalse("barrera: el Cliente llegó por la URL y el modal no tiene nada escrito");

        await EscribirAsync(cut, "Identificación fiscal", "B50999999");

        ElNavegadorAvisaAlRecargar(cut).Should().BeTrue();
    }

    [Fact]
    public async Task Lo_tecleado_mientras_cargan_las_Empresas_del_encadenado_si_es_un_cambio()
    {
        var puerta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mediador.PuertaSegundaCargaEmpresas = puerta;
        var cut = Renderizar($"centros?accion=crear&nombre=Planta%20Norte&clienteId={ClienteId}&empresaId={EmpresaId}");
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty(
            "el drawer ya está abierto y editable mientras carga las Empresas del Cliente"));

        await EscribirAsync(cut, "Dirección", "Polígono Plaza, nave 4");
        await cut.InvokeAsync(puerta.SetResult);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Montajes Ebro S.L."));

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Crear_el_Cliente_desde_el_modal_si_es_un_cambio_del_alta_de_centro()
    {
        var cut = await AbrirAltaAsync(Renderizar());
        await CrearClienteDesdeElSelectorAsync(cut, "Hierros Aragón");
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Crear").ClickAsync(new MouseEventArgs());

        _mediador.Enviadas.OfType<CrearClienteCommand>().Should().ContainSingle("barrera: el Cliente se creó");
        cut.FindAll("h2").Should().NotContain(h => h.TextContent.Trim() == "Nuevo cliente");
        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
        PreguntasDeSalida(cut).Should().Be(1);
    }
}
