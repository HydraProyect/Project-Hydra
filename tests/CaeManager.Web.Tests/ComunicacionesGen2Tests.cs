using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacion;
using CaeManager.Application.Comunicaciones.Eventos;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Comunicaciones.Queries.ObtenerMacros;
using CaeManager.Application.Comunicaciones.Queries.ObtenerMensajesBuzonPersonal;
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
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    public ComunicacionesGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

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

        public Func<Guid, ConversacionDetalleDto?> Detalle { get; set; } = _ => null;

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
                ObtenerMacrosQuery => new List<MacroListaDto>(),
                ObtenerCentrosParaSelectorQuery => new List<CentroSelectorDto>(),
                ObtenerTiposDocumentoQuery => new List<TipoDocumentoListaDto>(),
                ObtenerTrabajadoresParaSelectorQuery => new List<TrabajadorSelectorDto>(),
                ObtenerTiempoGestionConversacionQuery => new TiempoGestionConversacionDto(false, 0, 0),
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
        pestanas.Select(p => p.TextContent.Trim()).Should().Equal(["Clientes empresariales", "Mi buzón personal"],
            "el contrato terminológico no admite «Clientes» a secas: la contraparte de una Relación Empresarial es el Cliente empresarial");
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
            .Should().BeEquivalentTo(["Sin Cliente empresarial asignado (Triage) (1)", "Refrielectric S.A. (2)"],
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
            "el cliente empresarial filtrado es Montajes Ebro: la lista de Refrielectric llegó tarde");
        cut.FindAll(".bandeja-lista .esqueleto-lista").Should().BeEmpty(
            "la carga vigente terminó: la que llegó tarde no puede dejar la lista en «cargando» tampoco");
    }

    /// <summary>
    /// Contrato terminológico TALVEG: la contraparte de una Relación
    /// Empresarial es el <b>Cliente empresarial</b>. «Cliente» a secas
    /// colisiona con el Cliente comercial TALVEG —quien contrata TALVEG— y con
    /// el Pagador; en una bandeja que agrupa el correo por contraparte, esa
    /// ambigüedad decide qué cree la persona que está mirando.
    /// </summary>
    [Fact]
    public void Los_rotulos_de_la_bandeja_nombran_al_Cliente_empresarial_sin_abreviar()
    {
        var (cut, _) = Renderizar(new Escenario());

        cut.Markup.Should()
            .Contain("Esperando al Cliente empresarial")
            .And.Contain("Todos los Clientes empresariales")
            .And.Contain("primer correo de un Cliente empresarial");

        cut.Markup.Should()
            .NotContain("Esperando cliente")
            .And.NotContain("Todos los clientes")
            .And.NotContain("Sin cliente asignado");
    }
}
