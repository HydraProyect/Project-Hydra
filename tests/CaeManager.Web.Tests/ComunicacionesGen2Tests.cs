using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacion;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Comunicaciones.Queries.DetectarActualizacionDocumentoDesdeAdjunto;
using CaeManager.Application.Comunicaciones.Queries.ObtenerBorradorPedirPrioridad;
using CaeManager.Application.Comunicaciones.Queries.ObtenerCompanerosGestorCae;
using CaeManager.Application.Comunicaciones.Queries.ObtenerFormatosRequeridosCentro;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Domain.Integraciones;
using CaeManager.Application.Comunicaciones.Eventos;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Comunicaciones.Queries.ObtenerMacros;
using CaeManager.Application.Comunicaciones.Queries.ObtenerMensajesBuzonPersonal;
using CaeManager.Application.Comunicaciones.Queries.ObtenerNotasInternasConversacion;
using CaeManager.Application.Telemetria.Queries.ObtenerTiempoGestionConversacion;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comunicaciones.Pages;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// La bandeja de <c>/comunicaciones</c> (<see cref="Bandeja"/>) contra su
/// mockup Gen 2 («Comunicaciones TALVEG.dc.html») y contra el contrato de
/// envío y concurrencia de la pantalla.
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> el marcado que el rediseño fija (cabecera de
/// la columna de la lista, toggle de ámbito como <c>tablist</c>, cabeceras de
/// grupo con recuento, caret y desglose); que los dos vacíos siguen siendo
/// distintos y que quitar los filtros los quita de la URL; que el envío
/// distingue éxito, fallo de negocio y validación sin perder lo escrito; que
/// dos clics no mandan dos correos; y que una respuesta que llega tarde —de la
/// lista o del detalle— no pisa a la que el gestor está mirando (mediador
/// controlado con <see cref="TaskCompletionSource{TResult}"/>).
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> la autorización ni el alcance de cartera (de los
/// handlers, en Application); que el correo salga de verdad por Microsoft
/// Graph; el aislamiento por tenant; ni el aspecto (CSS). Tampoco el Buzón de
/// <c>/comunicaciones/buzon</c>, que es otra pantalla.
/// </para>
/// </summary>
public class ComunicacionesGen2Tests : BunitContext
{
    /// <summary><see cref="Drawer"/> y <see cref="Modal"/> importan dialogo-foco.js; el medidor de tiempo, su propio módulo.</summary>
    public ComunicacionesGen2Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
        // El Unified Timeline y el composer de notas internas leen sus textos del localizador.
        Services.AddLocalization();
    }

    private static readonly Guid ClienteRefrielectricId = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid ClienteEbroId = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");

    private static readonly ClienteSelectorDto ClienteRefrielectric = new(ClienteRefrielectricId, "Refrielectric S.A.");
    private static readonly ClienteSelectorDto ClienteEbro = new(ClienteEbroId, "Montajes Ebro S.L.");

    // ---------------------------------------------------------------- dobles

    private sealed class MediadorControlado(Func<object, Task<object?>> responder) : IMediator
    {
        public List<object> Enviados { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return (TResponse)(await responder(request))!;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Enviados.Add(request!);
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

    private sealed class TenantActualFalso : ITenantActual
    {
        /// <summary>Sin tenant resuelto el directorio devuelve vacío sin consultar nada — ver <see cref="CrearDirectorio"/>.</summary>
        public Guid? TenantId => null;
    }

    /// <summary>Nadie se suscribe sin tenant resuelto; existe para que la inyección no falle.</summary>
    private sealed class NotificadorQueNadieDebeTocar : INotificadorMensajesTiempoReal
    {
        public IDisposable Suscribir(Guid tenantId, Func<MensajeWhatsAppRecibidoEvent, Task> callback) =>
            throw new NotSupportedException("Sin tenant resuelto la página no se suscribe; si esto salta, cambió el camino.");

        public Task PublicarAsync(MensajeWhatsAppRecibidoEvent aviso, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AlmacenUsuariosQueNadieDebeTocar : IUserStore<ApplicationUser>
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Sin tenant resuelto no se consulta ningún usuario; si esto salta, cambió el camino.");

        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public void Dispose() { }
    }

    private sealed class TenantsQueryContextQueNadieDebeTocar : ITenantsQueryContext
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Sin tenant resuelto el directorio devuelve vacío sin consultar; si esto salta, cambió el camino.");

        public IQueryable<Tenant> Tenants => throw NoDeberia();
        public IQueryable<DelegacionTenant> DelegacionesTenant => throw NoDeberia();
        public IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => throw NoDeberia();
        public IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte => throw NoDeberia();
    }

    /// <summary>
    /// <c>DirectorioUsuariosTenant</c> es una clase concreta, no una interfaz:
    /// se construye con dobles que dejan clara la única ruta que esta pantalla
    /// recorre —sin tenant resuelto, cero ejecutivos y ninguna consulta—. El
    /// selector de ejecutivo, que es lo único que alimenta, no es lo que estos
    /// tests miden.
    /// </summary>
    private static DirectorioUsuariosTenant CrearDirectorio()
    {
        var tenantActual = new TenantActualFalso();
        var identidad = new CaeManagerDbContext(
            new DbContextOptionsBuilder<CaeManagerDbContext>().Options,
            DataProtectionProvider.Create(nameof(ComunicacionesGen2Tests)),
            tenantActual);

        var userManager = new UserManager<ApplicationUser>(
            new AlmacenUsuariosQueNadieDebeTocar(), null!, null!, null!, null!, null!, null!, null!, null!);

        return new DirectorioUsuariosTenant(
            userManager, new TenantsQueryContextQueNadieDebeTocar(), tenantActual, new PuertaAccesoDatos(), identidad);
    }

    // ---------------------------------------------------------------- escenario

    /// <summary>
    /// Datos que ve el mediador. <c>ObtenerConversacionesQuery</c> responde
    /// <b>según sus filtros</b>, como el handler real: un doble que los
    /// ignorase dejaría en verde una pantalla que no los envía.
    /// </summary>
    private sealed class Escenario
    {
        public List<ClienteSelectorDto> Clientes { get; } = [ClienteRefrielectric, ClienteEbro];

        public List<ConversacionListaDto> Conversaciones { get; } = [];

        public List<MensajeBuzonPersonalDto> BuzonPersonal { get; } = [];

        public List<MacroListaDto> Macros { get; } = [];

        /// <summary>Lo que devolvería <c>ObtenerCompanerosGestorCaeQuery</c>: ya acotado por Application, la página no filtra.</summary>
        public List<CompaneroGestorCaeDto> Companeros { get; } = [];

        public Func<Guid, ConversacionDetalleDto?> Detalle { get; set; } = _ => null;

        public List<NotaInternaDetalleDto> NotasInternas { get; } = [];

        public Func<ResponderConversacionCommand, Result> AlResponder { get; set; } = _ => Result.Exito();

        /// <summary>Si devuelve una tarea, esa petición se resuelve cuando el test lo diga.</summary>
        public Func<object, Task<object?>?> Interceptar { get; set; } = _ => null;

        public Task<object?> Responder(object peticion) =>
            Interceptar(peticion) ?? Task.FromResult<object?>(peticion switch
            {
                ObtenerClientesParaSelectorQuery => Clientes.ToList(),
                ObtenerConversacionesQuery q => Pagina(q),
                ObtenerMensajesBuzonPersonalQuery => BuzonPersonal.ToList(),
                ObtenerConversacionPorIdQuery q => Detalle(q.Id),
                ObtenerClientePorIdQuery q => new ClienteDetalleDto(
                    q.Id, Clientes.First(c => c.Id == q.Id).RazonSocial, "A11111111", false, null, DateTime.UtcNow, null, Guid.NewGuid()),
                ObtenerMacrosQuery => Macros.ToList(),
                ObtenerCompanerosGestorCaeQuery => Companeros.ToList(),
                ObtenerCentrosParaSelectorQuery => new List<CentroSelectorDto>(),
                ObtenerTiposDocumentoQuery => new List<TipoDocumentoListaDto>(),
                ObtenerTrabajadoresParaSelectorQuery => new List<TrabajadorSelectorDto>(),
                ObtenerTiempoGestionConversacionQuery => new TiempoGestionConversacionDto(false, 0, 0),
                ObtenerNotasInternasConversacionQuery => NotasInternas.ToList(),
                ResponderConversacionCommand c => AlResponder(c),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
            });

        /// <summary>Los filtros que esta pantalla manda de verdad; el resto de la consulta no los usan estos tests.</summary>
        public ResultadoPaginado<ConversacionListaDto> Pagina(ObtenerConversacionesQuery q)
        {
            var visibles = Conversaciones
                .Where(c => q.ClienteId is null || c.ClienteId == q.ClienteId)
                .Where(c => q.Estado is null || c.Estado == q.Estado)
                .Where(c => string.IsNullOrWhiteSpace(q.Busqueda)
                    || c.Asunto.Contains(q.Busqueda, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return new ResultadoPaginado<ConversacionListaDto>(visibles, visibles.Count, q.Pagina, q.TamanoPagina);
        }
    }

    private static ConversacionListaDto Conversacion(
        string asunto, ClienteSelectorDto? cliente = null, string remitente = "carmen.ruiz@refrielectric.es",
        EstadoConversacion estado = EstadoConversacion.Abierta, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), cliente?.Id, cliente?.RazonSocial, asunto, estado, null, remitente,
            "Adjunto la documentación.", DateTime.UtcNow, 2, CanalConversacion.Correo, null, DireccionMensaje.Entrante);

    private static ConversacionDetalleDto DetalleDe(ConversacionListaDto conversacion) =>
        new(conversacion.Id, conversacion.ClienteId, conversacion.ClienteRazonSocial, conversacion.Asunto,
            conversacion.Estado, null, null, conversacion.FechaUltimoMensajeUtc,
            [new MensajeDetalleDto(Guid.NewGuid(), DireccionMensaje.Entrante, CanalConversacion.Correo,
                conversacion.RemitentePrincipal, "<p>Adjunto la documentación.</p>", conversacion.FechaUltimoMensajeUtc, [], null, [])],
            [], [], []);

    // ---------------------------------------------------------------- arnés

    private (IRenderedComponent<Bandeja> Cut, MediadorControlado Mediador) Renderizar(Escenario escenario, string url = "comunicaciones")
    {
        var mediador = new MediadorControlado(escenario.Responder);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddSingleton<ILogger<Bandeja>>(_ => NullLogger<Bandeja>.Instance);
        Services.AddScoped<ITenantActual, TenantActualFalso>();
        Services.AddScoped<INotificadorMensajesTiempoReal, NotificadorQueNadieDebeTocar>();
        Services.AddScoped(_ => CrearDirectorio());
        // Un caso que fija el reloj lo registra antes de renderizar; el resto usa el del sistema.
        Services.TryAddSingleton(TimeProvider.System);

        // El módulo está congelado por defecto: sin esto la página navega a
        // /not-found y el test observaría una pantalla que no es.
        Services.AddSingleton<IOptions<ComunicacionesOptions>>(
            Options.Create(new ComunicacionesOptions { Activo = true }));

        // La URL se acciona antes de renderizar y no se teclea en el buscador:
        // ese campo rebota 300 ms y deja vivo un temporizador que se traga el
        // clic siguiente (medido en TiposDocumentoVacioPorFiltroTests).
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);

        return (Render<Bandeja>(), mediador);
    }

    private static IElement SelectorDeEstado(IRenderedComponent<Bandeja> cut) =>
        cut.FindAll(".bandeja-filtros-avanzados select")[0];

    private static IElement SelectorDeCliente(IRenderedComponent<Bandeja> cut) =>
        cut.FindAll(".bandeja-filtros-avanzados select")[1];

    private static IReadOnlyList<IElement> CabecerasGrupo(IRenderedComponent<Bandeja> cut) =>
        cut.FindAll(".bandeja-grupo-cabecera");

    private static IElement CabeceraGrupo(IRenderedComponent<Bandeja> cut, string rotulo) =>
        CabecerasGrupo(cut).Single(c => c.QuerySelector(".bandeja-grupo-titulo")!.TextContent.Trim() == rotulo);

    private static IReadOnlyList<string> AsuntosVisibles(IRenderedComponent<Bandeja> cut) =>
        cut.FindAll(".bandeja-fila-asunto").Select(f => f.TextContent.Trim()).ToList();

    private static Task SeleccionarFila(IRenderedComponent<Bandeja> cut, string asunto) =>
        cut.FindAll(".bandeja-fila")
            .Single(f => f.QuerySelector(".bandeja-fila-asunto")!.TextContent.Trim() == asunto)
            .ClickAsync(new MouseEventArgs());

    private static IElement Boton(IRenderedComponent<Bandeja> cut, string selector, string texto) =>
        cut.FindAll(selector).Single(b => b.TextContent.Trim() == texto);

    private static Task Enviar(IRenderedComponent<Bandeja> cut) =>
        Boton(cut, ".composer-acciones button", "Enviar").ClickAsync(new MouseEventArgs());

    private static Task Escribir(IRenderedComponent<Bandeja> cut, string texto) =>
        cut.Find(".composer-correo textarea").InputAsync(new ChangeEventArgs { Value = texto });

    // ---------------------------------------------------------------- mockup Gen 2

    [Fact]
    public void El_titulo_y_Redactar_viven_en_la_cabecera_de_la_columna_de_la_lista()
    {
        var (cut, _) = Renderizar(new Escenario());

        var cabecera = cut.Find(".bandeja-lista-cabecera");
        cabecera.QuerySelector("h1")!.TextContent.Trim().Should().Be("Comunicaciones");
        cabecera.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Redactar");

        cut.FindAll(".bandeja-toolbar").Should().BeEmpty(
            "el mockup Gen 2 no tiene banda de cabecera sobre las tres columnas: el título bajó a la lista");
    }

    [Fact]
    public async Task El_toggle_de_ambito_es_un_tablist_y_cambiar_a_Mi_buzon_pide_el_buzon_personal()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.Add(Conversacion("Documentación pendiente", ClienteRefrielectric));
        var (cut, mediador) = Renderizar(escenario);

        var pestanas = cut.FindAll("[role=tablist] [role=tab]");
        pestanas.Select(p => p.TextContent.Trim()).Should().Equal(["Clientes", "Mi buzón personal"],
            "decisión de rótulo del 2026-10-09: la contraparte de una Relación Empresarial, el Cliente empresarial, se rotula «Clientes»");
        pestanas[0].GetAttribute("aria-selected").Should().Be("true");
        pestanas[1].GetAttribute("aria-selected").Should().Be("false");

        await pestanas[1].ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<ObtenerMensajesBuzonPersonalQuery>().Should().ContainSingle(
            "el toggle vive dentro de la bandeja: cambia de ámbito, no navega a otra pantalla");
        cut.FindAll("[role=tablist] [role=tab]")[1].GetAttribute("aria-selected").Should().Be("true");
        cut.FindAll(".bandeja-filtros-form").Should().BeEmpty("los filtros de conversaciones no aplican al buzón personal");
    }

    [Fact]
    public async Task La_cabecera_de_grupo_lleva_el_recuento_el_desglose_y_pliega_el_grupo()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.AddRange(
        [
            Conversacion("Documentación pendiente", ClienteRefrielectric),
            Conversacion("RNT de julio", ClienteRefrielectric, "luis.otero@refrielectric.es"),
            Conversacion("Rechazo de documento", remitente: "notificaciones@nalanda.es"),
        ]);
        var (cut, _) = Renderizar(escenario);

        CabecerasGrupo(cut).Select(c => c.QuerySelector(".bandeja-grupo-titulo")!.TextContent.Trim())
            .Should().BeEquivalentTo(["Sin Cliente asignado (Triage) (1)", "Refrielectric S.A. (2)"],
                "el recuento va en el rótulo, como en el mockup");

        var deRefrielectric = CabeceraGrupo(cut, "Refrielectric S.A. (2)");
        deRefrielectric.GetAttribute("title").Should()
            .Be("2 conversaciones — luis.otero@refrielectric.es: RNT de julio · carmen.ruiz@refrielectric.es: Documentación pendiente",
                "el desglose del title dice CUÁLES son, que es justo lo que no se ve con el grupo plegado");
        deRefrielectric.GetAttribute("aria-expanded").Should().Be("true");

        await deRefrielectric.ClickAsync(new MouseEventArgs());

        CabeceraGrupo(cut, "Refrielectric S.A. (2)").GetAttribute("aria-expanded").Should().Be("false");
        AsuntosVisibles(cut).Should().Equal(["Rechazo de documento"],
            "plegado el grupo desaparecen sus filas, no las del otro");
    }

    [Fact]
    public void La_fila_pone_el_remitente_y_el_asunto_en_lineas_propias_y_la_lista_trae_su_divisor()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.Add(Conversacion("Documentación pendiente", ClienteRefrielectric));
        var (cut, _) = Renderizar(escenario);

        var fila = cut.Find(".bandeja-fila");
        fila.QuerySelector(".bandeja-fila-linea1 .bandeja-fila-remitente")!.TextContent.Trim().Should().Be("Refrielectric S.A.");
        fila.QuerySelector(".bandeja-fila-linea2 .bandeja-fila-asunto")!.TextContent.Trim().Should().Be("Documentación pendiente");
        fila.QuerySelector(".bandeja-fila-separador").Should().BeNull("remitente y asunto ya no comparten línea");

        var divisor = cut.Find(".bandeja-layout > [role=separator][data-redimensionable]");
        divisor.GetAttribute("data-redimensionable").Should().Be("--bandeja-lista-ancho");
        divisor.GetAttribute("tabindex").Should().Be("0", "el ancho también se ajusta con el teclado");
    }

    // ---------------------------------------------------------------- vacíos y filtros

    [Fact]
    public void Sin_ninguna_conversacion_el_vacio_no_culpa_a_unos_filtros_que_nadie_puso()
    {
        var (cut, _) = Renderizar(new Escenario());

        cut.Find(".bandeja-lista .estado-vacio h3").TextContent.Trim().Should().Be("No hay conversaciones");
        cut.Markup.Should().NotContain("Ninguna conversación con estos filtros");
        cut.FindAll(".bandeja-lista .estado-vacio button").Should().BeEmpty(
            "sin filtros puestos no hay ningún filtro que quitar");
    }

    [Fact]
    public async Task Filtrando_por_estado_el_vacio_lo_dice_y_quitar_los_filtros_los_quita_de_la_URL()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.Add(Conversacion("Documentación pendiente", ClienteRefrielectric));
        var (cut, mediador) = Renderizar(escenario, "comunicaciones?estado=Resuelta");
        var navegacion = Services.GetRequiredService<NavigationManager>();

        mediador.Enviados.OfType<ObtenerConversacionesQuery>().Last().Estado.Should().Be(EstadoConversacion.Resuelta,
            "el filtro de la URL tiene que llegar al servidor, no quedarse pintado en el select");
        cut.Find(".bandeja-lista .estado-vacio h3").TextContent.Trim()
            .Should().Be("Ninguna conversación con estos filtros");

        await Boton(cut, ".bandeja-lista .estado-vacio button", "Quitar los filtros").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().NotContain("estado=", "un filtro quitado no puede seguir en la URL que se comparte");
        mediador.Enviados.OfType<ObtenerConversacionesQuery>().Last().Estado.Should().BeNull();
        AsuntosVisibles(cut).Should().Equal(["Documentación pendiente"]);
    }

    [Fact]
    public async Task El_filtro_de_estado_viaja_a_la_URL_al_elegirlo()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.Add(Conversacion("Documentación pendiente", ClienteRefrielectric));
        var (cut, mediador) = Renderizar(escenario);
        var navegacion = Services.GetRequiredService<NavigationManager>();

        await SelectorDeEstado(cut).ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoConversacion.Resuelta) });

        navegacion.Uri.Should().Contain("estado=Resuelta", "la URL manda: es lo que se comparte y lo que se recarga");
        mediador.Enviados.OfType<ObtenerConversacionesQuery>().Last().Estado.Should().Be(EstadoConversacion.Resuelta);
        AsuntosVisibles(cut).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- notas internas

    [Fact]
    public async Task Abrir_un_hilo_pide_sus_notas_internas_y_las_pinta_con_el_composer_de_notas()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        escenario.NotasInternas.Add(new NotaInternaDetalleDto(Guid.NewGuid(), Guid.NewGuid(), "Llamar antes de las 10.", DateTime.UtcNow));

        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");

        mediador.Enviados.OfType<ObtenerNotasInternasConversacionQuery>().Should().ContainSingle()
            .Which.ConversacionId.Should().Be(conversacion.Id);
        cut.Find(".timeline-nota-interna-cuerpo").TextContent.Should().Be("Llamar antes de las 10.");
        cut.FindAll(".composer-nota-interna").Should().ContainSingle();
    }

    // ---------------------------------------------------------------- envío

    [Fact]
    public async Task Un_doble_clic_en_enviar_manda_un_solo_correo()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var envio = new TaskCompletionSource<object?>();
        escenario.Interceptar = p => p is ResponderConversacionCommand ? envio.Task : null;

        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        await Escribir(cut, "Recibido, gracias.");

        // Ninguno de los dos clics se espera antes de comprobar: sin guarda, el
        // segundo también quedaría retenido en «envio» y el test se colgaría en
        // vez de caer por el motivo que mide.
        var primero = Enviar(cut);
        var segundo = Enviar(cut);

        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().ContainSingle(
            "el segundo clic llega mientras el primero espera al servidor: sin guarda salen dos correos iguales");

        await cut.InvokeAsync(() => envio.SetResult(Result.Exito()));
        await primero;
        await segundo;

        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().ContainSingle(
            "el segundo clic se descartó, no quedó en cola");
    }

    [Fact]
    public async Task Un_fallo_de_negocio_al_enviar_dice_el_motivo_y_no_pierde_lo_escrito()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario
        {
            Detalle = _ => DetalleDe(conversacion),
            AlResponder = _ => Result.Fallo(Error.Crear("Conversacion.SinConexion", "No hay ningún buzón conectado para responder."))
        };
        escenario.Conversaciones.Add(conversacion);
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        await Escribir(cut, "Recibido, gracias.");

        await Enviar(cut);

        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle()
            .Which.Mensaje.Should().Be("No hay ningún buzón conectado para responder.");

        // El <textarea> del DOM no sirve de testigo: CampoTextarea no lo
        // vuelve a pintar al teclear (el navegador va por su cuenta), así que
        // su texto es "" tanto si la página conserva la respuesta como si la
        // ha borrado. El testigo que SÍ distingue los dos casos es reintentar:
        // si se hubiera perdido, el botón estaría deshabilitado y no saldría
        // ningún comando.
        Boton(cut, ".composer-acciones button", "Enviar").HasAttribute("disabled").Should().BeFalse(
            "con la respuesta borrada el botón se deshabilita solo");

        await Enviar(cut);

        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().HaveCount(2)
            .And.OnlyContain(c => c.CuerpoHtml == "Recibido, gracias.",
                "el segundo intento sale con lo mismo que se escribió: no hubo que reescribirlo");
    }

    [Fact]
    public async Task Una_validacion_al_enviar_se_distingue_del_fallo_generico()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario
        {
            Detalle = _ => DetalleDe(conversacion),
            AlResponder = _ => throw new ValidationException(
                [new ValidationFailure(nameof(ResponderConversacionCommand.CuerpoHtml), "El cuerpo no puede superar 10.000 caracteres.")])
        };
        escenario.Conversaciones.Add(conversacion);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        await Escribir(cut, "Recibido, gracias.");

        await Enviar(cut);

        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle()
            .Which.Mensaje.Should().Be("El cuerpo no puede superar 10.000 caracteres.",
                "el servidor dice qué está mal: repetirlo es útil, «intenta nuevamente» invita a fallar otra vez igual");
        Boton(cut, ".composer-acciones button", "Enviar").HasAttribute("disabled").Should().BeFalse(
            "una validación tampoco puede llevarse por delante lo escrito");
    }

    // ---------------------------------------------------------------- carreras

    [Fact]
    public async Task El_detalle_del_hilo_anterior_que_llega_tarde_no_pisa_al_abierto()
    {
        var primera = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var segunda = Conversacion("Rechazo del portal", ClienteEbro, "notificaciones@nalanda.es");
        var escenario = new Escenario();
        escenario.Conversaciones.AddRange([primera, segunda]);

        var detallePrimera = new TaskCompletionSource<object?>();
        var detalleSegunda = new TaskCompletionSource<object?>();
        escenario.Interceptar = p => p switch
        {
            ObtenerConversacionPorIdQuery q when q.Id == primera.Id => detallePrimera.Task,
            ObtenerConversacionPorIdQuery q when q.Id == segunda.Id => detalleSegunda.Task,
            _ => null
        };
        var (cut, _) = Renderizar(escenario);

        var aperturaPrimera = SeleccionarFila(cut, "Documentación pendiente");
        var aperturaSegunda = SeleccionarFila(cut, "Rechazo del portal");

        // La segunda responde primero; la primera, que ya no es la abierta, después.
        await cut.InvokeAsync(() => detalleSegunda.SetResult(DetalleDe(segunda)));
        await aperturaSegunda;
        await cut.InvokeAsync(() => detallePrimera.SetResult(DetalleDe(primera)));
        await aperturaPrimera;

        cut.Find(".bandeja-centro-titulo-fila h2").TextContent.Trim().Should().Be("Rechazo del portal",
            "el hilo abierto es el segundo: el detalle del primero llegó tarde y no es el suyo");
        cut.Find(".bandeja-cliente-identidad h3").TextContent.Trim().Should().Be("Montajes Ebro S.L.",
            "el panel de contexto tiene que ser el del hilo abierto, no el del que respondió tarde");
    }

    /// <summary>
    /// La guarda del detalle protege DOS tramos —el hilo y, tras otro await, su
    /// contexto de cliente— y el primero tapa al segundo cuando lo que se
    /// retiene es la consulta del hilo. Aquí se retiene la del cliente, que es
    /// el único camino por el que el segundo tramo decide algo.
    /// </summary>
    [Fact]
    public async Task El_contexto_del_cliente_que_llega_tarde_no_pisa_al_del_hilo_abierto()
    {
        var primera = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var segunda = Conversacion("Rechazo del portal", ClienteEbro, "notificaciones@nalanda.es");
        var escenario = new Escenario { Detalle = id => DetalleDe(id == primera.Id ? primera : segunda) };
        escenario.Conversaciones.AddRange([primera, segunda]);

        var clienteRefrielectric = new TaskCompletionSource<object?>();
        var clienteEbro = new TaskCompletionSource<object?>();
        escenario.Interceptar = p => p switch
        {
            ObtenerClientePorIdQuery q when q.Id == ClienteRefrielectricId => clienteRefrielectric.Task,
            ObtenerClientePorIdQuery q when q.Id == ClienteEbroId => clienteEbro.Task,
            _ => null
        };
        var (cut, _) = Renderizar(escenario);

        var aperturaPrimera = SeleccionarFila(cut, "Documentación pendiente");
        var aperturaSegunda = SeleccionarFila(cut, "Rechazo del portal");

        await cut.InvokeAsync(() => clienteEbro.SetResult(
            new ClienteDetalleDto(ClienteEbroId, "Montajes Ebro S.L.", "B22222222", false, null, DateTime.UtcNow, null, Guid.NewGuid())));
        await aperturaSegunda;
        await cut.InvokeAsync(() => clienteRefrielectric.SetResult(
            new ClienteDetalleDto(ClienteRefrielectricId, "Refrielectric S.A.", "A11111111", false, null, DateTime.UtcNow, null, Guid.NewGuid())));
        await aperturaPrimera;

        cut.Find(".bandeja-cliente-identidad h3").TextContent.Trim().Should().Be("Montajes Ebro S.L.",
            "el contexto pintado es el del hilo abierto, no el del hilo que se cerró antes de que su cliente respondiera");
    }

    [Fact]
    public async Task La_lista_del_filtro_anterior_que_llega_tarde_no_pisa_a_la_vigente()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.AddRange(
        [
            Conversacion("Solo de Refrielectric", ClienteRefrielectric),
            Conversacion("Solo de Ebro", ClienteEbro, "compras@montajesebro.es"),
        ]);

        var listaRefrielectric = new TaskCompletionSource<object?>();
        var listaEbro = new TaskCompletionSource<object?>();
        escenario.Interceptar = p => p switch
        {
            ObtenerConversacionesQuery q when q.ClienteId == ClienteRefrielectricId => listaRefrielectric.Task,
            ObtenerConversacionesQuery q when q.ClienteId == ClienteEbroId => listaEbro.Task,
            _ => null
        };
        var (cut, _) = Renderizar(escenario);

        var seleccion = cut.FindAll(".bandeja-filtros-avanzados select")[1];
        var filtroRefrielectric = seleccion.ChangeAsync(new ChangeEventArgs { Value = ClienteRefrielectricId.ToString() });
        var filtroEbro = cut.FindAll(".bandeja-filtros-avanzados select")[1]
            .ChangeAsync(new ChangeEventArgs { Value = ClienteEbroId.ToString() });

        await cut.InvokeAsync(() => listaEbro.SetResult(
            escenario.Pagina(new ObtenerConversacionesQuery(ClienteId: ClienteEbroId))));
        await filtroEbro;
        await cut.InvokeAsync(() => listaRefrielectric.SetResult(
            escenario.Pagina(new ObtenerConversacionesQuery(ClienteId: ClienteRefrielectricId))));
        await filtroRefrielectric;

        AsuntosVisibles(cut).Should().Equal(["Solo de Ebro"],
            "el Cliente filtrado es Montajes Ebro: la lista de Refrielectric llegó tarde");
        cut.FindAll(".bandeja-lista .esqueleto-lista").Should().BeEmpty(
            "la carga vigente terminó: la que llegó tarde no puede dejar la lista en «cargando» tampoco");
    }

    /// <summary>
    /// Contrato terminológico TALVEG: la contraparte de una Relación
    /// Empresarial es el <b>Cliente empresarial</b>, que en pantalla se rotula
    /// «Cliente» (decisión de rótulo del 2026-10-09): con mayúscula y artículo,
    /// nunca las formas sueltas anteriores ni el rótulo largo.
    /// </summary>
    [Fact]
    public void Los_rotulos_de_la_bandeja_nombran_al_Cliente_con_su_rotulo_de_pantalla()
    {
        var (cut, _) = Renderizar(new Escenario());

        cut.Markup.Should()
            .Contain("Esperando al Cliente")
            .And.Contain("Todos los Clientes")
            .And.Contain("primer correo de un Cliente");

        cut.Markup.Should()
            .NotContain("Esperando cliente")
            .And.NotContain("Todos los clientes")
            .And.NotContain("Sin cliente asignado")
            .And.NotContainEquivalentOf("cliente empresarial");
    }

    // ---------------------------------------------------------------- cambios sin guardar (P1-E2b)

    private static readonly Guid CentroNorteId = Guid.NewGuid();
    private static readonly Guid AdjuntoId = Guid.NewGuid();

    /// <summary>
    /// Una conversación de Refrielectric con un adjunto, un Centro para «Pedir prioridad» y un
    /// buzón para «Redactar»: lo mínimo para abrir los cinco formularios de la página.
    /// </summary>
    private async Task<(IRenderedComponent<Bandeja> Cut, NavigationManager Navegacion)> RenderizarConversacionAbiertaAsync(
        bool laDeteccionLeeLaFechaDeEmision = true, bool conBuzon = true)
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var detalle = DetalleDe(conversacion);
        detalle = detalle with
        {
            Mensajes = [detalle.Mensajes[0] with { Adjuntos = [new AdjuntoDetalleDto(AdjuntoId, "tc2.pdf", "application/pdf", 2048)] }],
        };
        var escenario = new Escenario
        {
            Detalle = _ => detalle,
            Interceptar = peticion => peticion switch
            {
                ObtenerCentrosParaSelectorQuery => Task.FromResult<object?>(new List<CentroSelectorDto>
                    { new(CentroNorteId, "Centro Norte", ClienteRefrielectric.RazonSocial, "Refrielectric S.A.") }),
                ObtenerFormatosRequeridosCentroQuery => Task.FromResult<object?>(null),
                ObtenerBorradorPedirPrioridadQuery => Task.FromResult<object?>(Result.Exito(new BorradorPedirPrioridadDto(
                    "validacion@example.invalid", "Prioridad Centro Norte", "<p>Rogamos prioridad.</p>", conBuzon, null, 2))),
                ObtenerConexionesIntegracionQuery when !conBuzon => Task.FromResult<object?>(new List<ConexionIntegracionListaDto>()),
                ObtenerConexionesIntegracionQuery => Task.FromResult<object?>(new List<ConexionIntegracionListaDto>
                    { new(Guid.NewGuid(), "cae@example.invalid", "CAE Norte", null, null, EstadoConexionIntegracion.Habilitada, DateTime.UtcNow, null, null) }),
                EnviarMensajeNuevoCommand => Task.FromResult<object?>(Result.Exito(Guid.NewGuid())),
                ObtenerEmpresasParaSelectorQuery => Task.FromResult<object?>(new List<EmpresaSelectorDto>()),
                DetectarActualizacionDocumentoDesdeAdjuntoQuery => Task.FromResult<object?>(Result.Exito(new DeteccionActualizacionDocumentoDto(
                    AdjuntoId, null, null, null, null, null, null, null,
                    laDeteccionLeeLaFechaDeEmision ? new DateOnly(2026, 9, 1) : null, null, 80))),
                _ => null,
            },
        };
        escenario.Conversaciones.Add(conversacion);

        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".composer-nota-interna").Should().ContainSingle());
        return (cut, Services.GetRequiredService<NavigationManager>());
    }

    private static Task EscribirRespuestaAsync(IRenderedComponent<Bandeja> cut, string texto) =>
        cut.FindAll(".composer-correo textarea").Single().InputAsync(new ChangeEventArgs { Value = texto });

    [Fact]
    public async Task Salir_con_la_respuesta_a_medias_pregunta()
    {
        var (cut, navegacion) = await RenderizarConversacionAbiertaAsync();
        await EscribirRespuestaAsync(cut, "Gracias, lo revisamos hoy.");

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
    }

    [Fact]
    public async Task Abrir_una_conversacion_sin_escribir_nada_y_salir_no_pregunta()
    {
        var (cut, navegacion) = await RenderizarConversacionAbiertaAsync();

        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion, "abrir una conversación no deja nada que perder");
    }

    /// <summary>
    /// Filtrar navega (los filtros viven en la URL) pero no sale de la página ni borra la
    /// respuesta a medias: esa navegación propia no pregunta y lo escrito sigue ahí.
    /// </summary>
    [Fact]
    public async Task Filtrar_con_la_respuesta_a_medias_no_pregunta_y_la_conserva()
    {
        var (cut, navegacion) = await RenderizarConversacionAbiertaAsync();
        await EscribirRespuestaAsync(cut, "Gracias, lo revisamos hoy.");

        await SelectorDeEstado(cut).ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoConversacion.Abierta) });

        navegacion.Uri.Should().Contain("estado=" + nameof(EstadoConversacion.Abierta));
        cut.FindAll(".modal-pie button").Should().NotContain(b => b.TextContent.Trim() == "Salir y descartar");
        cut.FindComponents<CampoTextarea>().Should().Contain(c => c.Instance.Valor == "Gracias, lo revisamos hoy.");
    }

    [Fact]
    public async Task Salir_con_la_nota_interna_a_medias_pregunta()
    {
        var (cut, navegacion) = await RenderizarConversacionAbiertaAsync();

        await cut.Find(".composer-nota-interna textarea").InputAsync(new ChangeEventArgs { Value = "Llamar antes de las 10." });

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
    }

    [Fact]
    public async Task Redactar_sin_tocar_no_pregunta_escribir_si_y_enviar_filtra_sin_preguntar()
    {
        var (cut, navegacion) = await RenderizarConversacionAbiertaAsync();
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Redactar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".drawer-panel").Should().ContainSingle("el test necesita el drawer de redactar abierto");
        await cut.Find(".drawer-panel button.drawer-cerrar").ClickAsync(new MouseEventArgs());
        cut.FindAll(".drawer-panel").Should().BeEmpty("sin cambios, la X cierra sin preguntar");

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Redactar").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".drawer-panel input.campo-input")[0].InputAsync(new ChangeEventArgs { Value = "contacto@example.invalid" });
        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
        await cut.PulsarEnElAvisoAsync("Seguir editando");

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Enviar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".drawer-panel").Should().BeEmpty();
        cut.FindAll(".modal-pie button").Should().NotContain(b => b.TextContent.Trim() == "Salir y descartar",
            "enviar recarga la lista navegando a la propia URL: lo escrito ya se envió");
        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion, "lo escrito ya se envió");
    }

    [Fact]
    public async Task Lo_detectado_en_el_adjunto_no_es_un_cambio_y_corregirlo_si()
    {
        var (cut, navegacion) = await RenderizarConversacionAbiertaAsync();
        await cut.Find(".timeline-adjunto-actualizar-documento").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".modal-actualizar-documento-formulario").Should().ContainSingle());
        cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "2026-09-01",
            "el test necesita que la detección haya rellenado la fecha de emisión");

        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion, "lo que rellenó la detección no es un cambio de quien revisa");
    }

    /// <summary>
    /// Sin fecha detectada, el formulario propone «hoy». Pasada la medianoche de Madrid
    /// (2026-09-27 22:30 UTC = 28 de septiembre, 00:30 en Madrid) tiene que proponer el
    /// día de negocio de Madrid, el mismo con el que Crear/RenovarDocumentoCommand
    /// juzgan que la fecha «no puede ser futura»; el día UTC sería todavía ayer. La zona
    /// local del reloj es UTC, como la de los contenedores.
    /// </summary>
    [Fact]
    public async Task Sin_fecha_detectada_pasada_la_medianoche_de_Madrid_propone_el_dia_de_Madrid()
    {
        Services.AddSingleton<TimeProvider>(new RelojEnUtc(new DateTime(2026, 9, 27, 22, 30, 0, DateTimeKind.Utc)));
        var (cut, _) = await RenderizarConversacionAbiertaAsync(laDeteccionLeeLaFechaDeEmision: false);
        await cut.Find(".timeline-adjunto-actualizar-documento").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".modal-actualizar-documento-formulario").Should().ContainSingle());

        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de emisión").Instance.Valor
            .Should().Be("2026-09-28", "en Madrid ya es 28; el 27 del día UTC sería ayer");
    }

    private sealed class RelojEnUtc(DateTime ahoraUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahoraUtc, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public async Task Corregir_lo_detectado_y_salir_pregunta()
    {
        var (cut, navegacion) = await RenderizarConversacionAbiertaAsync();
        await cut.Find(".timeline-adjunto-actualizar-documento").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".modal-actualizar-documento-formulario").Should().ContainSingle());

        await cut.Find(".modal-actualizar-documento-formulario textarea").InputAsync(new ChangeEventArgs { Value = "Renovado en septiembre." });

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
    }

    /// <summary>
    /// FS-15: «Redactar» sin buzón de Microsoft 365 conectado era un toast de error que se iba solo. Ahora el Drawer se
    /// abre con el motivo y a quién pedírselo, sin formulario y sin un «Enviar» que no puede enviar.
    /// </summary>
    [Fact]
    public async Task Redactar_sin_buzon_conectado_abre_el_aviso_con_su_salida_en_lugar_de_un_toast()
    {
        var (cut, _) = await RenderizarConversacionAbiertaAsync(conBuzon: false);

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Redactar").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find(".drawer-panel .aviso-sin-buzon").TextContent.Should().Contain("no hay ningún buzón de Microsoft 365 conectado"));
        cut.Find(".drawer-panel .aviso-sin-buzon-pedir").TextContent.Should().Contain("rol Administrador de esta organización");
        cut.FindAll(".drawer-panel button").Select(b => b.TextContent.Trim()).Should().NotContain("Enviar");
        cut.FindAll(".drawer-panel input.campo-input").Should().BeEmpty();
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty();
    }

    [Fact]
    public async Task Pedir_prioridad_sin_buzon_conectado_deja_copiar_el_borrador_y_dice_a_quien_pedir_el_buzon()
    {
        var (cut, _) = await RenderizarConversacionAbiertaAsync(conBuzon: false);
        await cut.FindAll(".composer-correo select")[1].ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Pedir prioridad de validación").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel .aviso-sin-buzon").Should().ContainSingle());
        cut.Find(".drawer-panel .aviso-sin-buzon-pedir").TextContent.Should().Contain("rol Administrador de esta organización");
        // El borrador es HTML y «Copiar» escribe texto plano: lo que se pega en el correo propio no lleva etiquetas.
        cut.FindComponent<AvisoSinBuzonCorreo>().FindComponent<BotonCopiar>().Instance.Valor.Should().Be("Prioridad Centro Norte\n\nRogamos prioridad.");
    }

    [Fact]
    public async Task El_borrador_de_pedir_prioridad_no_es_un_cambio_y_editarlo_si()
    {
        var (cut, navegacion) = await RenderizarConversacionAbiertaAsync();
        await cut.FindAll(".composer-correo select")[1].ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Pedir prioridad de validación").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "Prioridad Centro Norte"));

        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion, "el borrador propuesto no es un cambio de quien lo revisa");
    }

    [Fact]
    public async Task Editar_el_borrador_de_pedir_prioridad_y_salir_pregunta()
    {
        var (cut, navegacion) = await RenderizarConversacionAbiertaAsync();
        await cut.FindAll(".composer-correo select")[1].ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Pedir prioridad de validación").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "Prioridad Centro Norte"));

        await cut.FindAll(".drawer-panel textarea").Single().InputAsync(new ChangeEventArgs { Value = "Otro texto." });

        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
    }

    // ---- D-05: «Cancelar» de los tres formularios de la Bandeja cierra como la X

    private static Task PulsarCancelarEnAsync(IRenderedComponent<Bandeja> cut, string pie) =>
        cut.FindAll(pie + " button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

    private static bool PreguntaDescartar(IRenderedComponent<Bandeja> cut) =>
        cut.FindAll("h2").Any(h => h.TextContent.Trim() == "¿Descartar cambios?");

    [Fact]
    public async Task Cancelar_Redactar_con_el_mensaje_a_medias_pregunta_y_sin_escribir_cierra()
    {
        var (cut, _) = await RenderizarConversacionAbiertaAsync();
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Redactar").ClickAsync(new MouseEventArgs());
        await PulsarCancelarEnAsync(cut, ".drawer-pie");
        cut.FindAll(".drawer-panel").Should().BeEmpty("sin cambios, Cancelar cierra directamente");
        PreguntaDescartar(cut).Should().BeFalse();

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Redactar").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".drawer-panel input.campo-input")[0].InputAsync(new ChangeEventArgs { Value = "contacto@example.invalid" });
        await PulsarCancelarEnAsync(cut, ".drawer-pie");

        PreguntaDescartar(cut).Should().BeTrue("con el mensaje a medias, Cancelar pregunta como la X");
        cut.FindAll(".drawer-panel").Should().ContainSingle();
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Descartar cambios").ClickAsync(new MouseEventArgs());
        cut.FindAll(".drawer-panel").Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelar_Pedir_prioridad_con_el_borrador_editado_pregunta_y_sin_tocar_cierra()
    {
        var (cut, _) = await RenderizarConversacionAbiertaAsync();
        await cut.FindAll(".composer-correo select")[1].ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Pedir prioridad de validación").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "Prioridad Centro Norte"));
        await PulsarCancelarEnAsync(cut, ".drawer-pie");
        cut.FindAll(".drawer-panel").Should().BeEmpty("el borrador propuesto sin tocar no es un cambio: Cancelar cierra");
        PreguntaDescartar(cut).Should().BeFalse();

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Pedir prioridad de validación").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "Prioridad Centro Norte"));
        await cut.FindAll(".drawer-panel textarea").Single().InputAsync(new ChangeEventArgs { Value = "Otro texto." });
        await PulsarCancelarEnAsync(cut, ".drawer-pie");

        PreguntaDescartar(cut).Should().BeTrue("con el borrador editado, Cancelar pregunta como la X");
        cut.FindAll(".drawer-panel").Should().ContainSingle();
    }

    [Fact]
    public async Task Cancelar_Actualizar_documento_con_lo_corregido_pregunta_y_sin_tocar_cierra()
    {
        var (cut, _) = await RenderizarConversacionAbiertaAsync();
        await cut.Find(".timeline-adjunto-actualizar-documento").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".modal-actualizar-documento-formulario").Should().ContainSingle());
        await PulsarCancelarEnAsync(cut, ".modal-pie");
        cut.FindAll(".modal-actualizar-documento-formulario").Should().BeEmpty("lo que rellenó la detección no es un cambio: Cancelar cierra");
        PreguntaDescartar(cut).Should().BeFalse();

        await cut.Find(".timeline-adjunto-actualizar-documento").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".modal-actualizar-documento-formulario").Should().ContainSingle());
        await cut.Find(".modal-actualizar-documento-formulario textarea").InputAsync(new ChangeEventArgs { Value = "Renovado en septiembre." });
        await PulsarCancelarEnAsync(cut, ".modal-pie");

        PreguntaDescartar(cut).Should().BeTrue("con lo corregido sin guardar, Cancelar pregunta como la X");
        cut.FindAll(".modal-actualizar-documento-formulario").Should().ContainSingle();
    }

    /// <summary>Dos conversaciones de Refrielectric, con la primera abierta.</summary>
    private async Task<(IRenderedComponent<Bandeja> Cut, NavigationManager Navegacion, MediadorControlado Mediador, ConversacionListaDto Otra)>
        RenderizarDosConversacionesAsync()
    {
        var primera = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var otra = Conversacion("Alta de trabajador", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = id => DetalleDe(id == primera.Id ? primera : otra) };
        escenario.Conversaciones.AddRange([primera, otra]);

        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".composer-nota-interna").Should().ContainSingle());
        return (cut, Services.GetRequiredService<NavigationManager>(), mediador, otra);
    }

    /// <summary>
    /// Revisión Codex (lote C, ronda 2): pulsar la conversación ya abierta con una nota a
    /// medias no pregunta ni la borra, y la siguiente salida real sigue preguntando.
    /// </summary>
    [Fact]
    public async Task Pulsar_la_conversacion_ya_abierta_no_pregunta_y_la_salida_sigue_protegida()
    {
        var (cut, navegacion, _, _) = await RenderizarDosConversacionesAsync();
        await cut.Find(".composer-nota-interna textarea").InputAsync(new ChangeEventArgs { Value = "Llamar antes de las 10." });

        // Sin esperar el clic: si preguntara, el manejador quedaría esperando la respuesta.
        var seleccion = SeleccionarFila(cut, "Documentación pendiente");

        cut.FindAll(".modal-pie button").Should().NotContain(b => b.TextContent.Trim() == "Salir y descartar");
        await seleccion;
        cut.FindComponents<CampoTextarea>().Should().Contain(c => c.Instance.Valor == "Llamar antes de las 10.");
        await cut.SalirYComprobarQuePreguntaAsync(navegacion);
    }

    /// <summary>
    /// Revisión Codex (lote C): elegir otra conversación con una nota a medias pregunta ANTES
    /// de cambiar la selección, la URL o el detalle; «Seguir editando» deja la nota y la
    /// conversación como estaban.
    /// </summary>
    [Fact]
    public async Task Elegir_otra_conversacion_con_la_nota_a_medias_pregunta_antes_y_seguir_la_conserva()
    {
        var (cut, navegacion, mediador, otra) = await RenderizarDosConversacionesAsync();
        await cut.Find(".composer-nota-interna textarea").InputAsync(new ChangeEventArgs { Value = "Llamar antes de las 10." });
        var origen = navegacion.Uri;

        var seleccion = SeleccionarFila(cut, "Alta de trabajador");

        cut.WaitForAssertion(() => cut.FindAll(".modal-pie button").Should().Contain(b => b.TextContent.Trim() == "Seguir editando"));
        mediador.Enviados.OfType<ObtenerConversacionPorIdQuery>().Should().NotContain(q => q.Id == otra.Id,
            "no se carga la otra conversación mientras se pregunta");
        navegacion.Uri.Should().Be(origen);

        await cut.PulsarEnElAvisoAsync("Seguir editando");
        await seleccion;

        mediador.Enviados.OfType<ObtenerConversacionPorIdQuery>().Should().NotContain(q => q.Id == otra.Id);
        navegacion.Uri.Should().Be(origen);
        cut.FindComponents<CampoTextarea>().Should().Contain(c => c.Instance.Valor == "Llamar antes de las 10.");
    }

    [Fact]
    public async Task Elegir_otra_conversacion_y_descartar_abre_la_otra()
    {
        var (cut, navegacion, mediador, otra) = await RenderizarDosConversacionesAsync();
        await EscribirRespuestaAsync(cut, "Gracias, lo revisamos hoy.");

        var seleccion = SeleccionarFila(cut, "Alta de trabajador");
        cut.WaitForAssertion(() => cut.FindAll(".modal-pie button").Should().Contain(b => b.TextContent.Trim() == "Salir y descartar"));
        await cut.PulsarEnElAvisoAsync("Salir y descartar");
        await seleccion;

        mediador.Enviados.OfType<ObtenerConversacionPorIdQuery>().Should().Contain(q => q.Id == otra.Id);
        navegacion.Uri.Should().Contain("conversacion=" + otra.Id);
        cut.FindComponents<CampoTextarea>().Should().NotContain(c => c.Instance.Valor == "Gracias, lo revisamos hoy.");
    }

    [Fact]
    public async Task Cambiar_a_Mi_buzon_personal_con_la_respuesta_a_medias_pregunta_antes()
    {
        var (cut, navegacion, _, _) = await RenderizarDosConversacionesAsync();
        await EscribirRespuestaAsync(cut, "Gracias, lo revisamos hoy.");
        var origen = navegacion.Uri;

        var cambio = cut.FindAll(".bandeja-toggle").Single(b => b.TextContent.Contains("personal", StringComparison.OrdinalIgnoreCase))
            .ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".modal-pie button").Should().Contain(b => b.TextContent.Trim() == "Seguir editando"));
        await cut.PulsarEnElAvisoAsync("Seguir editando");
        await cambio;

        navegacion.Uri.Should().Be(origen);
        cut.FindComponents<CampoTextarea>().Should().Contain(c => c.Instance.Valor == "Gracias, lo revisamos hoy.");
    }

    /// <summary>
    /// Revisión Codex (lote C): el aviso del navegador al recargar o cerrar la pestaña se
    /// calcula en el render de la página; escribir la nota en su composer tiene que
    /// re-renderizarla.
    /// </summary>
    [Fact]
    public async Task Escribir_la_nota_interna_arma_el_aviso_del_navegador()
    {
        var (cut, _, _, _) = await RenderizarDosConversacionesAsync();
        cut.FindComponent<NavigationLock>().Instance.ConfirmExternalNavigation.Should().BeFalse();

        await cut.Find(".composer-nota-interna textarea").InputAsync(new ChangeEventArgs { Value = "Llamar antes de las 10." });

        cut.FindComponent<NavigationLock>().Instance.ConfirmExternalNavigation.Should().BeTrue(
            "recargar o cerrar la pestaña con la nota a medias tiene que avisar");
    }

    // ---------------------------------------------------------------- macro, Ctrl+Intro, j/k y errores accionables

    private static IElement TextareaCorreo(IRenderedComponent<Bandeja> cut) => cut.Find(".composer-correo textarea");

    private static Task Teclear(IElement elemento, string tecla, bool ctrl = false, bool meta = false) =>
        elemento.KeyDownAsync(new KeyboardEventArgs { Key = tecla, CtrlKey = ctrl, MetaKey = meta });

    [Fact]
    public async Task Aplicar_una_macro_con_un_borrador_escrito_lo_conserva_y_anade_la_macro_al_final()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var macro = new MacroListaDto(Guid.NewGuid(), null, null, "Acuse", "Gracias, lo revisamos hoy.", Guid.NewGuid());
        escenario.Macros.Add(macro);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        await Escribir(cut, "Hola Carmen,");

        await cut.Find(".composer-macro-campo select").ChangeAsync(new ChangeEventArgs { Value = macro.Id.ToString() });

        var texto = TextareaCorreo(cut).TextContent;
        texto.Should().StartWith("Hola Carmen,", "el borrador no se pierde");
        texto.Should().EndWith("Gracias, lo revisamos hoy.", "la macro se añade al final");
    }

    [Fact]
    public async Task Aplicar_una_macro_sin_borrador_deja_solo_la_macro()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var macro = new MacroListaDto(Guid.NewGuid(), null, null, "Acuse", "Gracias, lo revisamos hoy.", Guid.NewGuid());
        escenario.Macros.Add(macro);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");

        await cut.Find(".composer-macro-campo select").ChangeAsync(new ChangeEventArgs { Value = macro.Id.ToString() });

        TextareaCorreo(cut).TextContent.Should().Be("Gracias, lo revisamos hoy.");
    }

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(false, false, 0)]
    public async Task Ctrl_o_Cmd_Intro_envian_el_correo_y_Intro_solo_no(bool ctrl, bool meta, int enviosEsperados)
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        await Escribir(cut, "Recibido, gracias.");

        await Teclear(TextareaCorreo(cut), "Enter", ctrl, meta);

        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().HaveCount(enviosEsperados);
    }

    [Fact]
    public async Task Ctrl_Intro_sin_texto_no_envia_nada()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");

        await Teclear(TextareaCorreo(cut), "Enter", ctrl: true);

        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Dos_Ctrl_Intro_seguidos_mandan_un_solo_correo()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var envio = new TaskCompletionSource<object?>();
        escenario.Interceptar = p => p is ResponderConversacionCommand ? envio.Task : null;
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        await Escribir(cut, "Recibido, gracias.");

        var primero = Teclear(TextareaCorreo(cut), "Enter", ctrl: true);
        var segundo = Teclear(TextareaCorreo(cut), "Enter", ctrl: true);

        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().ContainSingle(
            "el segundo atajo llega con el primer envío en curso");

        await cut.InvokeAsync(() => envio.SetResult(Result.Exito()));
        await primero;
        await segundo;

        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().ContainSingle();
    }

    private static Task PulsarAtajoDeLista(IRenderedComponent<Bandeja> cut, string tecla) =>
        cut.InvokeAsync(() => cut.FindComponent<AtajosListaTeclado>().Instance.RecibirAtajo(tecla));

    private static IReadOnlyList<string> AsuntosEnfocados(IRenderedComponent<Bandeja> cut) =>
        cut.FindAll(".bandeja-fila.fila-enfocada .bandeja-fila-asunto").Select(f => f.TextContent.Trim()).ToList();

    [Fact]
    public async Task J_y_k_mueven_el_foco_por_la_lista_y_Intro_abre_la_conversacion_enfocada()
    {
        var primera = Conversacion("Primera", ClienteRefrielectric);
        var segunda = Conversacion("Segunda", ClienteEbro);
        var escenario = new Escenario { Detalle = id => DetalleDe(id == primera.Id ? primera : segunda) };
        escenario.Conversaciones.Add(primera);
        escenario.Conversaciones.Add(segunda);
        var (cut, mediador) = Renderizar(escenario);
        var orden = AsuntosVisibles(cut);

        AsuntosEnfocados(cut).Should().BeEmpty("hasta pulsar j no hay fila enfocada");

        await PulsarAtajoDeLista(cut, "j");
        AsuntosEnfocados(cut).Should().Equal(orden[0]);

        await PulsarAtajoDeLista(cut, "j");
        AsuntosEnfocados(cut).Should().Equal(orden[1]);

        await PulsarAtajoDeLista(cut, "j");
        AsuntosEnfocados(cut).Should().ContainSingle().Which.Should().Be(orden[1], "j no pasa del final");

        await PulsarAtajoDeLista(cut, "k");
        AsuntosEnfocados(cut).Should().Equal(orden[0]);

        await PulsarAtajoDeLista(cut, "Enter");
        var abierta = orden[0] == "Primera" ? primera : segunda;
        mediador.Enviados.OfType<ObtenerConversacionPorIdQuery>().Should().ContainSingle().Which.Id.Should().Be(abierta.Id);
    }

    [Fact]
    public async Task Intro_sin_fila_enfocada_no_abre_nada()
    {
        var conversacion = Conversacion("Primera", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var (cut, mediador) = Renderizar(escenario);

        await PulsarAtajoDeLista(cut, "Enter");

        mediador.Enviados.OfType<ObtenerConversacionPorIdQuery>().Should().BeEmpty();
    }

    [Fact]
    public async Task Un_fallo_inesperado_al_enviar_dice_que_fallo_y_que_el_texto_sigue_en_el_redactor()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario
        {
            Detalle = _ => DetalleDe(conversacion),
            AlResponder = _ => throw new InvalidOperationException("boom")
        };
        escenario.Conversaciones.Add(conversacion);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        await Escribir(cut, "Recibido, gracias.");

        await Enviar(cut);

        var mensaje = Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Subject.Mensaje;
        mensaje.Should().StartWith("No se envió la respuesta.").And.Contain("siguen en el redactor");
        mensaje.Should().NotContain("Intenta nuevamente");
    }

    // ---------------------------------------------------------------- Contactar con un compañero

    private static readonly CompaneroGestorCaeDto Marta = new(Guid.NewGuid(), "Marta Solà", "marta@operador.test", "+34 600 111 222");
    private static readonly CompaneroGestorCaeDto PereSinTelefono = new(Guid.NewGuid(), "Pere Vidal", "pere@operador.test", null);

    private static IReadOnlyList<IElement> FilasDeCompaneros(IRenderedComponent<Bandeja> cut) =>
        cut.FindAll(".companero");

    [Fact]
    public void Sin_companeros_la_seccion_no_se_pinta_y_la_bandeja_pide_la_lista_una_vez()
    {
        var (cut, mediador) = Renderizar(new Escenario());

        cut.FindAll(".companeros").Should().BeEmpty("quien no es Gestor CAE recibe la lista vacía y no ve la sección");
        mediador.Enviados.OfType<ObtenerCompanerosGestorCaeQuery>().Should().ContainSingle();
    }

    [Fact]
    public void Con_companeros_el_desplegable_muestra_nombre_correo_y_telefono_cada_uno_con_su_boton_de_copiar()
    {
        var escenario = new Escenario();
        escenario.Companeros.AddRange([Marta, PereSinTelefono]);
        var (cut, _) = Renderizar(escenario);

        cut.Find(".companeros summary").TextContent.Trim().Should().Be("Contactar con un compañero");
        var filas = FilasDeCompaneros(cut);
        filas.Should().HaveCount(2);

        filas[0].TextContent.Should().Contain("Marta Solà").And.Contain("marta@operador.test").And.Contain("+34 600 111 222");
        filas[0].QuerySelectorAll("button.boton-copiar").Select(b => b.TextContent.Trim())
            .Should().Equal("Copiar correo", "Copiar teléfono");

        // Dos «Copiar correo» iguales en la lista no se distinguen por el rótulo: el nombre accesible lleva a quién pertenece.
        filas[0].QuerySelectorAll("button.boton-copiar").Select(b => b.GetAttribute("aria-label"))
            .Should().Equal("Copiar el correo de Marta Solà", "Copiar el teléfono de Marta Solà");
        filas[1].QuerySelectorAll("button.boton-copiar").Select(b => b.GetAttribute("aria-label"))
            .Should().Equal("Copiar el correo de Pere Vidal");

        filas[1].TextContent.Should().Contain("Pere Vidal").And.Contain("pere@operador.test")
            .And.Contain("Sin teléfono registrado");
        filas[1].QuerySelectorAll("button.boton-copiar").Select(b => b.TextContent.Trim())
            .Should().Equal("Copiar correo");
    }

    [Fact]
    public async Task Copiar_el_telefono_lo_manda_al_portapapeles_y_lo_anuncia_con_el_nombre_del_companero()
    {
        var modulo = JSInterop.SetupModule("./js/clipboard.js");
        modulo.SetupVoid("copiarAlPortapapeles", _ => true).SetVoidResult();
        var escenario = new Escenario();
        escenario.Companeros.Add(Marta);
        var (cut, _) = Renderizar(escenario);

        var boton = FilasDeCompaneros(cut)[0].QuerySelectorAll("button.boton-copiar").Single(b => b.TextContent.Trim() == "Copiar teléfono");
        await boton.ClickAsync(new MouseEventArgs());

        modulo.VerifyInvoke("copiarAlPortapapeles").Arguments.Should().Equal("+34 600 111 222");
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle()
            .Which.Mensaje.Should().Be("Se copió el teléfono de Marta Solà al portapapeles.");
    }

    [Fact]
    public void Si_la_lectura_de_companeros_falla_la_bandeja_sigue_con_su_lista_y_sin_la_seccion()
    {
        var escenario = new Escenario
        {
            Interceptar = p => p is ObtenerCompanerosGestorCaeQuery
                ? Task.FromException<object?>(new InvalidOperationException("boom"))
                : null
        };
        escenario.Conversaciones.Add(Conversacion("Documentación pendiente", ClienteRefrielectric));
        var (cut, _) = Renderizar(escenario);

        cut.FindAll(".companeros").Should().BeEmpty();
        AsuntosVisibles(cut).Should().ContainSingle().Which.Should().Be("Documentación pendiente");
    }

    [Fact]
    public void En_catalan_los_textos_de_la_seccion_salen_en_catalan()
    {
        var (previa, previaUi) = (System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CultureInfo.CurrentUICulture);
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.CurrentUICulture =
            System.Globalization.CultureInfo.GetCultureInfo("ca-ES");
        try
        {
            var escenario = new Escenario();
            escenario.Companeros.Add(PereSinTelefono);
            var (cut, _) = Renderizar(escenario);

            cut.Find(".companeros summary").TextContent.Trim().Should().Be("Contactar amb un company");
            var fila = FilasDeCompaneros(cut).Single();
            fila.TextContent.Should().Contain("Correu").And.Contain("Sense telèfon registrat");
            fila.QuerySelectorAll("button.boton-copiar").Select(b => b.TextContent.Trim()).Should().Equal("Copia el correu");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previa;
            System.Globalization.CultureInfo.CurrentUICulture = previaUi;
        }
    }
}
