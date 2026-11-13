using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto;
using CaeManager.Application.Comunicaciones.Commands.AsignarClienteConversacion;
using CaeManager.Application.Comunicaciones.Commands.CambiarPrioridadConversacion;
using CaeManager.Application.Comunicaciones.Commands.ConfirmarClasificacionRuidoMensaje;
using CaeManager.Application.Comunicaciones.Commands.DescartarSugerenciaGestion;
using CaeManager.Application.Comunicaciones.Commands.DescartarSugerenciaVisita;
using CaeManager.Application.Comunicaciones.Commands.VincularConversacion;
using CaeManager.Application.Gestiones.Commands.CrearGestionesParaTrabajador;
using CaeManager.Application.Telemetria.Commands.RegistrarTramoGestion;
using CaeManager.Application.Comunicaciones.Commands.AplicarMacro;
using CaeManager.Application.Comunicaciones.Commands.CambiarEstadoConversacion;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacion;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Comunicaciones.Queries.DetectarActualizacionDocumentoDesdeAdjunto;
using CaeManager.Application.Comunicaciones.Commands.PedirPrioridadValidacion;
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
using CaeManager.Application.Comunicaciones.Queries.ObtenerRecordatorioPropuestoConversacion;
using CaeManager.Application.Comunicaciones.Queries.ObtenerRespuestaSugerida;
using CaeManager.Application.Comunicaciones.Commands.GenerarRespuestaSugerida;
using CaeManager.Application.Comunicaciones.Commands.ResolverRespuestaSugerida;
using CaeManager.Application.Contactos.Commands.FijarNotaContactoAgenda;
using CaeManager.Application.Contactos.Queries.ObtenerNotaContacto;
using CaeManager.Web.Features.Comunicaciones.Components;
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
using Microsoft.AspNetCore.Components.Forms;
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
/// la columna de la lista, toggle de ámbito como <c>tablist</c>, tabla inicial y lista plana del hilo); que los dos vacíos siguen siendo
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
            userManager, new TenantsQueryContextQueNadieDebeTocar(), tenantActual, new PuertaAccesoDatos(), identidad, null!);
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
        private readonly List<(Guid ConversacionId, EventoDetalleDto Evento)> _plantillasAplicadas = [];

        /// <summary>Lo que devolvería <c>ObtenerCompanerosGestorCaeQuery</c>: ya acotado por Application, la página no filtra.</summary>
        public List<CompaneroGestorCaeDto> Companeros { get; } = [];

        public Func<Guid, ConversacionDetalleDto?> Detalle { get; set; } = _ => null;

        public List<NotaInternaDetalleDto> NotasInternas { get; } = [];

        public Func<ResponderConversacionCommand, Result> AlResponder { get; set; } = _ => Result.Exito();

        /// <summary>La sugerencia activa del hilo: <c>ResolverRespuestaSugeridaCommand</c> la mueve de estado como el handler real.</summary>
        public RespuestaSugeridaDto? Sugerencia { get; set; }

        public Result<Guid> ResultadoGenerar { get; set; } =
            Result.Fallo<Guid>(Error.Crear("Sugerencia.NoDisponible", "Sin proveedor de IA."));

        /// <summary>Lo que existe tras pedir una sugerencia con éxito.</summary>
        public RespuestaSugeridaDto? SugerenciaAlGenerar { get; set; }

        public int Generaciones { get; private set; }

        private Result<Guid> GenerarSugerencia()
        {
            Generaciones++;
            if (ResultadoGenerar.EsExitoso) Sugerencia = SugerenciaAlGenerar;
            return ResultadoGenerar;
        }

        public List<AccionRespuestaSugerida> AccionesSobreSugerencia { get; } = [];

        public NotaContactoDto? NotaContacto { get; set; }

        public List<FijarNotaContactoAgendaCommand> NotasFijadas { get; } = [];

        private Result ResolverSugerencia(ResolverRespuestaSugeridaCommand comando)
        {
            AccionesSobreSugerencia.Add(comando.Accion);
            Sugerencia = Sugerencia is null ? null : comando.Accion switch
            {
                AccionRespuestaSugerida.Insertar => Sugerencia with { Estado = EstadoRespuestaSugerida.SinRevisar },
                AccionRespuestaSugerida.Confirmar => Sugerencia with { Estado = EstadoRespuestaSugerida.Revisada },
                _ => null
            };
            return Result.Exito();
        }

        private Result GuardarNotaContacto(FijarNotaContactoAgendaCommand comando)
        {
            NotasFijadas.Add(comando);
            NotaContacto = NotaContacto is null ? null : NotaContacto with { Notas = comando.Texto, ActualizadaEnUtc = DateTime.UtcNow };
            return Result.Exito();
        }

        /// <summary>Si devuelve una tarea, esa petición se resuelve cuando el test lo diga.</summary>
        public Func<object, Task<object?>?> Interceptar { get; set; } = _ => null;

        public Task<object?> Responder(object peticion) =>
            Interceptar(peticion) ?? Task.FromResult<object?>(peticion switch
            {
                ObtenerClientesParaSelectorQuery => Clientes.ToList(),
                ObtenerConversacionesQuery q => Pagina(q),
                ObtenerMensajesBuzonPersonalQuery => BuzonPersonal.ToList(),
                ObtenerConversacionPorIdQuery q => DetalleConPlantillas(q.Id),
                ObtenerClientePorIdQuery q => new ClienteDetalleDto(
                    q.Id, Clientes.First(c => c.Id == q.Id).RazonSocial, "A11111111", false, null, DateTime.UtcNow, null, Guid.NewGuid()),
                ObtenerMacrosQuery => Macros.ToList(),
                AplicarMacroCommand c => AplicarPlantilla(c),
                ObtenerCompanerosGestorCaeQuery => Companeros.ToList(),
                ObtenerCentrosParaSelectorQuery => new List<CentroSelectorDto>(),
                ObtenerTiposDocumentoQuery => new List<TipoDocumentoListaDto>(),
                ObtenerTrabajadoresParaSelectorQuery => new List<TrabajadorSelectorDto>(),
                ObtenerTiempoGestionConversacionQuery => new TiempoGestionConversacionDto(false, 0, 0),
                ObtenerNotasInternasConversacionQuery => NotasInternas.ToList(),
                CaeManager.Application.Comunicaciones.AlertasCriticas.ObtenerAlertaCriticaQuery => null,
                ObtenerRecordatorioPropuestoConversacionQuery => null,
                ObtenerRespuestaSugeridaQuery => Sugerencia,
                ObtenerNotaContactoQuery => NotaContacto,
                GenerarRespuestaSugeridaCommand => GenerarSugerencia(),
                ResolverRespuestaSugeridaCommand c => ResolverSugerencia(c),
                FijarNotaContactoAgendaCommand c => GuardarNotaContacto(c),
                ResponderConversacionCommand c => AlResponder(c),
                CambiarEstadoConversacionCommand => Result.Exito(),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
            });

        private Result<string> AplicarPlantilla(AplicarMacroCommand comando)
        {
            var plantilla = Macros.Single(m => m.Id == comando.MacroId);
            _plantillasAplicadas.Add((comando.ConversacionId,
                new EventoDetalleDto(Guid.NewGuid(), TipoEventoConversacion.PlantillaAplicada, plantilla.Id,
                    DateTime.UtcNow, $"Se aplicó la plantilla {plantilla.Titulo} al borrador.")));
            return Result.Exito(plantilla.CuerpoHtml);
        }

        private ConversacionDetalleDto? DetalleConPlantillas(Guid id)
        {
            var detalle = Detalle(id);
            return detalle is null ? null : detalle with
            {
                Eventos = detalle.Eventos.Concat(_plantillasAplicadas.Where(p => p.ConversacionId == id).Select(p => p.Evento)).ToList()
            };
        }

        /// <summary>Los filtros que esta pantalla manda de verdad; el resto de la consulta no los usan estos tests.</summary>
        public ResultadoPaginado<ConversacionListaDto> Pagina(ObtenerConversacionesQuery q)
        {
            var visibles = Conversaciones
                .Where(c => q.ClienteId is null || c.ClienteId == q.ClienteId)
                .Where(c => q.Estado is null || c.Estado == q.Estado)
                .Where(c => string.IsNullOrWhiteSpace(q.Busqueda)
                    || c.Asunto.Contains(q.Busqueda, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return new ResultadoPaginado<ConversacionListaDto>(
                visibles.Skip((q.Pagina - 1) * q.TamanoPagina).Take(q.TamanoPagina).ToList(), visibles.Count, q.Pagina, q.TamanoPagina);
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

    private (IRenderedComponent<Bandeja> Cut, MediadorControlado Mediador) Renderizar(
        Escenario escenario, string url = "comunicaciones", bool medidorActivo = false)
    {
        var mediador = new MediadorControlado(escenario.Responder);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddSingleton<ILogger<Bandeja>>(_ => NullLogger<Bandeja>.Instance);
        Services.AddSingleton<ILogger<RecordatorioPropuesto>>(_ => NullLogger<RecordatorioPropuesto>.Instance);
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

        // Con la medición activa, el medidor consulta RendererInfo: no interactivo, no carga su módulo JS (el tramo se mide igual).
        if (medidorActivo) SetRendererInfo(new RendererInfo("Server", isInteractive: false));

        return (Render<Bandeja>(), mediador);
    }

    private static IElement SelectorDeEstado(IRenderedComponent<Bandeja> cut) =>
        cut.FindAll(".bandeja-filtros-avanzados select")[0];

    /// <summary>El selector de estado de la cabecera del detalle, no el filtro de la lista.</summary>
    private static IElement SelectorCambiarEstado(IRenderedComponent<Bandeja> cut) =>
        cut.FindAll(".bandeja-centro-acciones select").Single(s => s.ParentElement!.QuerySelector("label")?.TextContent.Trim() == "Estado");

    private static IElement SelectorDeCliente(IRenderedComponent<Bandeja> cut) =>
        cut.FindAll(".bandeja-filtros-avanzados select")
            .Single(s => s.QuerySelector($"option[value='{ClienteEbroId}']") is not null);

    private static IReadOnlyList<string> AsuntosVisibles(IRenderedComponent<Bandeja> cut) =>
        cut.FindAll(".bandeja-fila-asunto, .tabla-conversaciones-asunto button").Select(f => f.TextContent.Trim()).ToList();

    private static Task SeleccionarFila(IRenderedComponent<Bandeja> cut, string asunto)
    {
        var tabla = cut.FindAll(".tabla-conversaciones-asunto button").FirstOrDefault(b => b.TextContent.Trim() == asunto);
        return (tabla ?? cut.FindAll(".bandeja-fila")
            .Single(f => f.QuerySelector(".bandeja-fila-asunto")!.TextContent.Trim() == asunto))
            .ClickAsync(new MouseEventArgs());
    }

    private static IElement Boton(IRenderedComponent<Bandeja> cut, string selector, string texto) =>
        cut.FindAll(selector).Single(b => b.TextContent.Trim() == texto);

    private static Task Enviar(IRenderedComponent<Bandeja> cut) =>
        Boton(cut, ".composer-acciones button", "Enviar").ClickAsync(new MouseEventArgs());

    private static Task Escribir(IRenderedComponent<Bandeja> cut, string texto) =>
        cut.Find(".composer-correo textarea").InputAsync(new ChangeEventArgs { Value = texto });

    // ---------------------------------------------------------------- revisión 1:1, incremento 2: E18 (respuesta sugerida)

    private const string TextoSugerido =
        "Buenos días, Carmen. Recibimos la documentación y la revisaremos hoy mismo; le confirmamos el resultado mañana por la mañana.";

    private static RespuestaSugeridaDto PropuestaPara(ConversacionDetalleDto detalle, string texto = TextoSugerido) =>
        new(Guid.NewGuid(), detalle.Id, detalle.Mensajes[0].Id, OrigenRespuestaSugerida.Vega, null, texto,
            EstadoRespuestaSugerida.Propuesta, Guid.NewGuid(), DateTime.UtcNow);

    private (Escenario Escenario, ConversacionDetalleDto Detalle) EscenarioConSugerencia()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var detalle = DetalleDe(conversacion);
        var escenario = new Escenario { Detalle = _ => detalle };
        escenario.Conversaciones.Add(conversacion);
        escenario.Sugerencia = PropuestaPara(detalle);
        return (escenario, detalle);
    }

    private static bool EnviarDeshabilitado(IRenderedComponent<Bandeja> cut) =>
        Boton(cut, ".composer-acciones button", "Enviar").HasAttribute("disabled");

    private static int Posicion<T>(MediadorControlado mediador) => mediador.Enviados.FindIndex(p => p is T);

    [Fact]
    public async Task La_respuesta_sugerida_solo_se_propone_y_no_envia_nada()
    {
        var (escenario, _) = EscenarioConSugerencia();
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");

        cut.WaitForAssertion(() => cut.Find(".rs-bloque").TextContent.Should().Contain("Respuesta sugerida por Vega").And.Contain("Buenos días, Carmen"));
        await Boton(cut, ".rs-bloque button", "Insertar para revisar").ClickAsync(new MouseEventArgs());

        escenario.AccionesSobreSugerencia.Should().Equal(AccionRespuestaSugerida.Insertar);
        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().BeEmpty("insertar coloca el texto en el redactor: no envía");
        cut.Find(".rs-sinrevisar").TextContent.Should().Contain("Sugerencia sin revisar");
    }

    [Fact]
    public async Task Una_sugerencia_sin_revisar_bloquea_Enviar_y_Ctrl_Intro_hasta_confirmarla()
    {
        var (escenario, _) = EscenarioConSugerencia();
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-bloque").Should().ContainSingle());
        await Boton(cut, ".rs-bloque button", "Insertar para revisar").ClickAsync(new MouseEventArgs());

        EnviarDeshabilitado(cut).Should().BeTrue("hay texto, pero es una sugerencia que nadie ha revisado");
        await cut.Find(".composer-correo").KeyDownAsync(new KeyboardEventArgs { Key = "Enter", CtrlKey = true });
        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().BeEmpty("Ctrl+Intro respeta el mismo bloqueo que el botón");

        await Boton(cut, ".rs-sinrevisar button", "Lo he revisado").ClickAsync(new MouseEventArgs());

        escenario.AccionesSobreSugerencia.Should().Equal(AccionRespuestaSugerida.Insertar, AccionRespuestaSugerida.Confirmar);
        cut.FindAll(".rs-sinrevisar").Should().BeEmpty();
        EnviarDeshabilitado(cut).Should().BeFalse();
        await Enviar(cut);
        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().ContainSingle();
    }

    [Fact]
    public async Task Descartar_una_sugerencia_sin_revisar_vacia_el_redactor_y_no_deja_enviar_su_texto()
    {
        var (escenario, _) = EscenarioConSugerencia();
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-bloque").Should().ContainSingle());
        await Boton(cut, ".rs-bloque button", "Insertar para revisar").ClickAsync(new MouseEventArgs());

        await Boton(cut, ".rs-sinrevisar button", "Descartar").ClickAsync(new MouseEventArgs());

        escenario.AccionesSobreSugerencia.Should().Equal(AccionRespuestaSugerida.Insertar, AccionRespuestaSugerida.Descartar);
        EnviarDeshabilitado(cut).Should().BeTrue("el redactor ya no tiene el texto descartado, que el servidor seguiría rechazando");
        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Cambiar_una_letra_de_la_sugerencia_no_basta_para_enviarla()
    {
        var (escenario, _) = EscenarioConSugerencia();
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-bloque").Should().ContainSingle());
        await Boton(cut, ".rs-bloque button", "Insertar para revisar").ClickAsync(new MouseEventArgs());

        await Escribir(cut, TextoSugerido + "!");

        EnviarDeshabilitado(cut).Should().BeTrue();
    }

    [Fact]
    public async Task Una_sugerencia_editada_mas_del_umbral_se_confirma_en_el_servidor_antes_de_enviar()
    {
        var (escenario, _) = EscenarioConSugerencia();
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-bloque").Should().ContainSingle());
        await Boton(cut, ".rs-bloque button", "Insertar para revisar").ClickAsync(new MouseEventArgs());

        await Escribir(cut, "Hola Carmen, recibido todo. Lo miramos esta tarde y mañana le decimos algo sin falta.");
        EnviarDeshabilitado(cut).Should().BeFalse("el texto se apartó más de 15 caracteres de lo sugerido");
        await Enviar(cut);

        escenario.AccionesSobreSugerencia.Should().Equal(AccionRespuestaSugerida.Insertar, AccionRespuestaSugerida.Confirmar);
        Posicion<ResolverRespuestaSugeridaCommand>(mediador).Should().BeGreaterThan(-1);
        mediador.Enviados.FindLastIndex(p => p is ResolverRespuestaSugeridaCommand { Accion: AccionRespuestaSugerida.Confirmar })
            .Should().BeLessThan(Posicion<ResponderConversacionCommand>(mediador), "el servidor bloquea el envío mientras siga sin revisar");
        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().ContainSingle();
    }

    [Fact]
    public async Task Sin_proveedor_de_IA_pedir_la_sugerencia_degrada_con_un_aviso_y_el_redactor_sigue_sirviendo()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-vacio").Should().ContainSingle());

        await Boton(cut, ".rs-vacio button", "Pedir respuesta sugerida").ClickAsync(new MouseEventArgs());

        escenario.Generaciones.Should().Be(1);
        cut.Find(".rs-aviso-error").TextContent.Should().Contain("Vega no ha podido redactar una respuesta ahora mismo");
        await Escribir(cut, "Recibido, gracias.");
        EnviarDeshabilitado(cut).Should().BeFalse("la IA es opcional: escribir a mano sigue funcionando");
        await Enviar(cut);
        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().ContainSingle();
    }

    [Fact]
    public async Task Pedir_una_sugerencia_la_muestra_sin_enviarla_y_descartarla_vuelve_a_ofrecer_pedirla()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var detalle = DetalleDe(conversacion);
        var escenario = new Escenario { Detalle = _ => detalle, ResultadoGenerar = Result.Exito(Guid.NewGuid()) };
        escenario.SugerenciaAlGenerar = PropuestaPara(detalle);
        escenario.Conversaciones.Add(conversacion);
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-vacio").Should().ContainSingle());

        await Boton(cut, ".rs-vacio button", "Pedir respuesta sugerida").ClickAsync(new MouseEventArgs());
        cut.Find(".rs-bloque").TextContent.Should().Contain("Buenos días, Carmen");
        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().BeEmpty();

        await Boton(cut, ".rs-bloque button", "Descartar").ClickAsync(new MouseEventArgs());

        escenario.AccionesSobreSugerencia.Should().Equal(AccionRespuestaSugerida.Descartar);
        cut.FindAll(".rs-vacio").Should().ContainSingle();
    }

    [Fact]
    public async Task Una_conversacion_resuelta_no_ofrece_pedir_una_sugerencia()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric, estado: EstadoConversacion.Resuelta);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");

        cut.WaitForAssertion(() => cut.FindAll(".bandeja-centro-acciones").Should().NotBeEmpty());
        cut.FindAll(".rs-bloque").Should().BeEmpty();
    }
    // ---------------------------------------------------------------- revisión 1:1, incremento 2: E21 (nota fija del contacto y notas del equipo)

    private static NotaContactoDto NotaDeCarmen(string? texto = "Prefiere que la llamen por la mañana.") =>
        new(Guid.NewGuid(), "Carmen Ruiz", texto, null, new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task La_nota_fija_del_contacto_se_muestra_con_su_origen_y_se_edita_sin_perder_el_resto()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion), NotaContacto = NotaDeCarmen() };
        escenario.Conversaciones.Add(conversacion);
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");

        cut.WaitForAssertion(() => cut.FindAll(".nota-fija").Should().ContainSingle());
        var nota = cut.Find(".nota-fija");
        nota.TextContent.Should().Contain("Nota fija · Carmen Ruiz").And.Contain("Prefiere que la llamen por la mañana.")
            .And.Contain("Agenda ·").And.Contain("03/11/2026");
        mediador.Enviados.OfType<ObtenerNotaContactoQuery>().Should().ContainSingle().Which.Email.Should().Be("carmen.ruiz@refrielectric.es");

        await Boton(cut, ".nota-fija button", "Editar").ClickAsync(new MouseEventArgs());
        await cut.Find(".nota-fija-area").InputAsync(new ChangeEventArgs { Value = "Llamar por la tarde." });
        await Boton(cut, ".nota-fija button", "Guardar nota fija").ClickAsync(new MouseEventArgs());

        escenario.NotasFijadas.Should().ContainSingle().Which.Texto.Should().Be("Llamar por la tarde.");
        cut.Find(".nota-fija").TextContent.Should().Contain("Llamar por la tarde.");
        cut.FindAll(".nota-fija-area").Should().BeEmpty("al guardar se sale de la edición");
    }

    [Fact]
    public async Task Cancelar_la_edicion_de_la_nota_fija_vaciada_conserva_la_anterior()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion), NotaContacto = NotaDeCarmen() };
        escenario.Conversaciones.Add(conversacion);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".nota-fija").Should().ContainSingle());

        await Boton(cut, ".nota-fija button", "Editar").ClickAsync(new MouseEventArgs());
        await cut.Find(".nota-fija-area").InputAsync(new ChangeEventArgs { Value = "   " });
        // Con el campo vacío sobre una nota existente, el botón retira (BAJO 4 de la r1 de la #2167); cancelar no retira nada.
        cut.FindAll(".nota-fija button").Should().NotContain(b => b.TextContent.Trim() == "Guardar nota fija");

        await Boton(cut, ".nota-fija button", "Cancelar").ClickAsync(new MouseEventArgs());

        escenario.NotasFijadas.Should().BeEmpty();
        cut.Find(".nota-fija").TextContent.Should().Contain("Prefiere que la llamen por la mañana.");
    }

    [Fact]
    public async Task Sin_contacto_en_la_agenda_no_se_pinta_la_nota_fija()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion), NotaContacto = null };
        escenario.Conversaciones.Add(conversacion);
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");

        cut.WaitForAssertion(() => mediador.Enviados.OfType<ObtenerNotaContactoQuery>().Should().NotBeEmpty());
        cut.FindAll(".nota-fija").Should().BeEmpty();
    }

    [Fact]
    public async Task Las_notas_del_equipo_enlazan_con_la_nota_dentro_del_hilo()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        var nota = new NotaInternaDetalleDto(Guid.NewGuid(), Guid.NewGuid(), "Pedir el TC2 antes del viernes.", DateTime.UtcNow);
        escenario.NotasInternas.Add(nota);
        escenario.Conversaciones.Add(conversacion);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");

        cut.WaitForAssertion(() => cut.FindAll(".notas-equipo").Should().ContainSingle());
        var enlace = cut.Find(".notas-equipo a.notas-equipo-nota");
        enlace.TextContent.Should().Contain("Pedir el TC2 antes del viernes.");
        enlace.GetAttribute("href").Should().StartWith("http").And.Contain("/comunicaciones").And.EndWith($"#nota-interna-{nota.Id}",
            "con <base href=\"/\"> un enlace de solo fragmento saldría de la pantalla: se ancla a la URL actual");
        cut.FindAll($"#nota-interna-{nota.Id}").Should().ContainSingle("el enlace apunta a una fila real del hilo");
    }

    // ---------------------------------------------------------------- revisiones Codex r1 a r3 de la #2163 (notas)

    private static Task<object?> Ya(object? valor) => Task.FromResult(valor);

    /// <summary>Dos conversaciones del mismo Cliente empresarial con dos contactos distintos de su Agenda (Carmen y Laura).</summary>
    private (Escenario Escenario, ConversacionListaDto Primera, ConversacionListaDto Segunda) DosConversacionesDeLaMismaAgenda()
    {
        var primera = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var segunda = Conversacion("Rechazo del portal", ClienteRefrielectric, "laura.gil@refrielectric.es");
        var detallePrimera = DetalleDe(primera);
        var detalleSegunda = DetalleDe(segunda);
        var escenario = new Escenario { Detalle = id => id == primera.Id ? detallePrimera : detalleSegunda };
        escenario.Conversaciones.AddRange([primera, segunda]);
        return (escenario, primera, segunda);
    }

    // ---------------------------------------------------------------- revisión Codex r1 de la #2163 (respuesta sugerida)

    private static string TextoDelRedactor(IRenderedComponent<Bandeja> cut) => cut.FindComponent<ComposerBar>().Instance.Texto;

    /// <summary>Las mismas dos conversaciones, con una sugerencia de Vega pendiente en la primera.</summary>
    private (Escenario Escenario, ConversacionListaDto Primera, ConversacionListaDto Segunda, ConversacionDetalleDto DetallePrimera)
        DosConversacionesConSugerenciaEnLaPrimera()
    {
        var (escenario, primera, segunda) = DosConversacionesDeLaMismaAgenda();
        var detallePrimera = escenario.Detalle(primera.Id)!;
        escenario.Sugerencia = PropuestaPara(detallePrimera);
        return (escenario, primera, segunda, detallePrimera);
    }

    /// <summary>
    /// ALTO 2: «Insertar» en A tarda; entretanto se abre B, cuyo detalle aún carga. La inserción de A vuelve con el detalle de
    /// A todavía en pantalla y antes comparaba solo ese Id: el texto de A acababa en el redactor de B.
    /// </summary>
    [Fact]
    public async Task La_insercion_tardia_de_una_sugerencia_no_escribe_en_el_redactor_de_otra_conversacion()
    {
        var (escenario, _, segunda, _) = DosConversacionesConSugerenciaEnLaPrimera();
        var insercion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detalleSegunda = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p switch
        {
            ResolverRespuestaSugeridaCommand { Accion: AccionRespuestaSugerida.Insertar } => insercion.Task,
            ObtenerConversacionPorIdQuery q when q.Id == segunda.Id => detalleSegunda.Task,
            ObtenerRespuestaSugeridaQuery q when q.ConversacionId == segunda.Id => Ya(null),
            _ => null
        };
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-bloque").Should().ContainSingle());

        var clic = Boton(cut, ".rs-bloque button", "Insertar para revisar").ClickAsync(new MouseEventArgs());
        var apertura = SeleccionarFila(cut, "Rechazo del portal");
        await cut.InvokeAsync(() => insercion.SetResult(Result.Exito()));
        await clic;
        await cut.InvokeAsync(() => detalleSegunda.SetResult(escenario.Detalle(segunda.Id)));
        await apertura;

        cut.Find(".bandeja-centro-titulo-fila h2").TextContent.Trim().Should().Be("Rechazo del portal");
        TextoDelRedactor(cut).Should().BeEmpty("la sugerencia era de la conversación anterior");
    }

    /// <summary>
    /// ALTO 3: Enviar en A espera a que el servidor confirme la sugerencia editada; entretanto se abre B y se escribe su
    /// borrador. Antes el comando se construía al volver de esa espera, con la selección y el redactor de B.
    /// </summary>
    [Fact]
    public async Task Un_envio_iniciado_en_una_conversacion_no_se_reanuda_con_el_destino_y_el_borrador_de_otra()
    {
        var (escenario, primera, segunda, _) = DosConversacionesConSugerenciaEnLaPrimera();
        var confirmacion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p switch
        {
            ResolverRespuestaSugeridaCommand { Accion: AccionRespuestaSugerida.Confirmar } => confirmacion.Task,
            ObtenerRespuestaSugeridaQuery q when q.ConversacionId == segunda.Id => Ya(null),
            _ => null
        };
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-bloque").Should().ContainSingle());
        await Boton(cut, ".rs-bloque button", "Insertar para revisar").ClickAsync(new MouseEventArgs());
        const string respuestaDeA = "Hola Carmen, recibido todo. Lo miramos esta tarde y mañana le decimos algo sin falta.";
        await Escribir(cut, respuestaDeA);

        var envio = Enviar(cut);
        var apertura = SeleccionarFila(cut, "Rechazo del portal");
        cut.WaitForAssertion(() => cut.FindAll(".modal-pie button").Should().Contain(b => b.TextContent.Trim() == "Salir y descartar"));
        await cut.PulsarEnElAvisoAsync("Salir y descartar");
        await apertura;
        cut.WaitForAssertion(() => cut.Find(".bandeja-centro-titulo-fila h2").TextContent.Trim().Should().Be("Rechazo del portal"));
        const string borradorDeB = "Laura, este borrador es de otra conversación y no se ha enviado.";
        await Escribir(cut, borradorDeB);

        await cut.InvokeAsync(() => confirmacion.SetResult(Result.Exito()));
        await envio;

        var enviado = mediador.Enviados.OfType<ResponderConversacionCommand>().Should().ContainSingle().Subject;
        enviado.ConversacionId.Should().Be(primera.Id, "el envío se pidió en A");
        enviado.CuerpoHtml.Should().Be(respuestaDeA);
        TextoDelRedactor(cut).Should().Be(borradorDeB, "el borrador de B no se envió y no se borra al terminar el de A");
    }

    // ---------------------------------------------------------------- revisión Codex r2 de la #2163

    /// <summary>
    /// MEDIO 1 (r2): «Insertar» en A tarda; entretanto se abre B y se vuelve a A, y se escribe un borrador nuevo. La inserción
    /// vuelve con A abierta otra vez, que es la misma conversación pero no la misma selección: no pisa el borrador nuevo.
    /// </summary>
    [Fact]
    public async Task La_insercion_tardia_no_pisa_el_borrador_nuevo_tras_ir_a_otra_conversacion_y_volver()
    {
        var (escenario, _, segunda, _) = DosConversacionesConSugerenciaEnLaPrimera();
        var insercion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p switch
        {
            ResolverRespuestaSugeridaCommand { Accion: AccionRespuestaSugerida.Insertar } => insercion.Task,
            ObtenerRespuestaSugeridaQuery q when q.ConversacionId == segunda.Id => Ya(null),
            _ => null
        };
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-bloque").Should().ContainSingle());

        var clic = Boton(cut, ".rs-bloque button", "Insertar para revisar").ClickAsync(new MouseEventArgs());
        await SeleccionarFila(cut, "Rechazo del portal");
        cut.WaitForAssertion(() => cut.Find(".bandeja-centro-titulo-fila h2").TextContent.Trim().Should().Be("Rechazo del portal"));
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.Find(".bandeja-centro-titulo-fila h2").TextContent.Trim().Should().Be("Documentación pendiente"));
        const string borradorNuevo = "Carmen, esto lo he escrito al volver a la conversación y no es la sugerencia.";
        await Escribir(cut, borradorNuevo);

        await cut.InvokeAsync(() => insercion.SetResult(Result.Exito()));
        await clic;

        TextoDelRedactor(cut).Should().Be(borradorNuevo, "la inserción se pidió en una selección anterior de esta conversación");
    }

    /// <summary>
    /// El token de selección del panel es la apertura de la Bandeja (fusión con la #2167): una recarga del mismo hilo, por un
    /// mensaje entrante mientras «Insertar» espera, no es otra apertura y la inserción llega al redactor.
    /// </summary>
    [Fact]
    public async Task La_insercion_que_termina_tras_una_recarga_del_mismo_hilo_llega_al_redactor()
    {
        var (escenario, primera, _, _) = DosConversacionesConSugerenciaEnLaPrimera();
        var insercion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p switch
        {
            ResolverRespuestaSugeridaCommand { Accion: AccionRespuestaSugerida.Insertar } => insercion.Task,
            _ => null
        };
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-bloque").Should().ContainSingle());

        var clic = Boton(cut, ".rs-bloque button", "Insertar para revisar").ClickAsync(new MouseEventArgs());
        var lecturas = mediador.Enviados.OfType<ObtenerConversacionPorIdQuery>().Count();
        await cut.InvokeAsync(() => (Task)typeof(Bandeja).GetMethod("AlRecibirMensajeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cut.Instance, [new MensajeWhatsAppRecibidoEvent(Guid.NewGuid(), primera.Id)])!);
        cut.WaitForAssertion(() => mediador.Enviados.OfType<ObtenerConversacionPorIdQuery>().Count().Should().BeGreaterThan(lecturas,
            "el mensaje entrante recarga el hilo abierto"));

        await cut.InvokeAsync(() => insercion.SetResult(Result.Exito()));
        await clic;

        cut.WaitForAssertion(() => TextoDelRedactor(cut).Should().Be(TextoSugerido,
            "una recarga de la conversación abierta no es otra apertura: la inserción pedida en ella se aplica"));
    }

    /// <summary>
    /// MEDIO 1 (r2): Enviar en A espera la confirmación de la sugerencia editada; entretanto se abre B, se vuelve a A y se
    /// escribe un borrador nuevo. Sale lo que se pidió enviar, y al terminar el borrador nuevo de A no se vacía.
    /// </summary>
    [Fact]
    public async Task Un_envio_que_termina_tras_ir_a_otra_conversacion_y_volver_no_vacia_el_borrador_nuevo()
    {
        var (escenario, primera, segunda, _) = DosConversacionesConSugerenciaEnLaPrimera();
        var confirmacion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p switch
        {
            ResolverRespuestaSugeridaCommand { Accion: AccionRespuestaSugerida.Confirmar } => confirmacion.Task,
            ObtenerRespuestaSugeridaQuery q when q.ConversacionId == segunda.Id => Ya(null),
            _ => null
        };
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-bloque").Should().ContainSingle());
        await Boton(cut, ".rs-bloque button", "Insertar para revisar").ClickAsync(new MouseEventArgs());
        const string respuestaDeA = "Hola Carmen, recibido todo. Lo miramos esta tarde y mañana le decimos algo sin falta.";
        await Escribir(cut, respuestaDeA);

        var envio = Enviar(cut);
        var apertura = SeleccionarFila(cut, "Rechazo del portal");
        cut.WaitForAssertion(() => cut.FindAll(".modal-pie button").Should().Contain(b => b.TextContent.Trim() == "Salir y descartar"));
        await cut.PulsarEnElAvisoAsync("Salir y descartar");
        await apertura;
        cut.WaitForAssertion(() => cut.Find(".bandeja-centro-titulo-fila h2").TextContent.Trim().Should().Be("Rechazo del portal"));
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.Find(".bandeja-centro-titulo-fila h2").TextContent.Trim().Should().Be("Documentación pendiente"));
        const string borradorNuevo = "Carmen, este es un borrador nuevo de la misma conversación y aún no se ha enviado.";
        await Escribir(cut, borradorNuevo);

        await cut.InvokeAsync(() => confirmacion.SetResult(Result.Exito()));
        await envio;

        var enviado = mediador.Enviados.OfType<ResponderConversacionCommand>().Should().ContainSingle().Subject;
        enviado.ConversacionId.Should().Be(primera.Id);
        enviado.CuerpoHtml.Should().Be(respuestaDeA, "sale lo que se pidió enviar");
        TextoDelRedactor(cut).Should().Be(borradorNuevo, "el borrador nuevo no es el que se envió y no se vacía al terminar");
    }

    // ---------------------------------------------------------------- revisión Codex r3 de la #2163

    /// <summary>
    /// ALTO (r3): otra persona insertó la sugerencia y sigue sin revisar. Aquí no se ha mostrado: una respuesta escrita aparte,
    /// por mucho que se aparte de ella, no la confirma al enviar, y «Lo he revisado» no se ofrece. Antes el envío la daba por
    /// revisada y otra sesión podía enviar después el texto original.
    /// </summary>
    [Fact]
    public async Task Una_respuesta_escrita_aparte_no_confirma_la_sugerencia_sin_revisar_que_inserto_otra_persona()
    {
        var (escenario, detalle) = EscenarioConSugerencia();
        escenario.Sugerencia = PropuestaPara(detalle) with { Estado = EstadoRespuestaSugerida.SinRevisar };
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-sinrevisar").Should().ContainSingle());

        await Escribir(cut, "Hola Carmen, recibido todo. Lo miramos esta tarde y mañana le decimos algo sin falta.");
        if (!EnviarDeshabilitado(cut)) await Enviar(cut);
        await cut.Find(".composer-correo").KeyDownAsync(new KeyboardEventArgs { Key = "Enter", CtrlKey = true });

        escenario.AccionesSobreSugerencia.Should().BeEmpty("una respuesta escrita aparte no es una edición de la sugerencia");
        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().BeEmpty();
        EnviarDeshabilitado(cut).Should().BeTrue("el servidor bloquea todo envío de la conversación mientras siga sin revisar");
        cut.FindAll(".rs-sinrevisar button").Should().NotContain(b => b.TextContent.Trim() == "Lo he revisado",
            "esta sugerencia no está en este redactor: nadie la ha leído aquí");
    }

    /// <summary>
    /// ALTO (r3), la salida: «Poner en redactor» con texto propio pregunta antes de sustituirlo; ya en el redactor, la
    /// sugerencia se ha mostrado aquí y se revisa como la insertada en esta apertura.
    /// </summary>
    [Fact]
    public async Task La_sugerencia_sin_revisar_de_otra_persona_se_revisa_tras_ponerla_en_el_redactor()
    {
        var (escenario, detalle) = EscenarioConSugerencia();
        escenario.Sugerencia = PropuestaPara(detalle) with { Estado = EstadoRespuestaSugerida.SinRevisar };
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-sinrevisar").Should().ContainSingle());
        await Escribir(cut, "Borrador propio que no es la sugerencia.");

        await Boton(cut, ".rs-sinrevisar button", "Poner en el redactor").ClickAsync(new MouseEventArgs());
        TextoDelRedactor(cut).Should().Be("Borrador propio que no es la sugerencia.", "con texto propio se pregunta antes de pisarlo");
        await Boton(cut, ".rs-sinrevisar button", "Conservar mi texto").ClickAsync(new MouseEventArgs());
        TextoDelRedactor(cut).Should().Be("Borrador propio que no es la sugerencia.", "conservar no toca el redactor");
        cut.FindAll(".rs-sinrevisar button").Should().NotContain(b => b.TextContent.Trim() == "Sustituir");
        EnviarDeshabilitado(cut).Should().BeTrue("sigue sin revisar y fuera del redactor");

        await Boton(cut, ".rs-sinrevisar button", "Poner en el redactor").ClickAsync(new MouseEventArgs());
        await Boton(cut, ".rs-sinrevisar button", "Sustituir").ClickAsync(new MouseEventArgs());

        TextoDelRedactor(cut).Should().Be(TextoSugerido);
        await Escribir(cut, "Hola Carmen, recibido todo. Lo miramos esta tarde y mañana le decimos algo sin falta.");
        EnviarDeshabilitado(cut).Should().BeFalse("ahora es una edición de la sugerencia mostrada aquí");
        await Enviar(cut);

        escenario.AccionesSobreSugerencia.Should().Equal(AccionRespuestaSugerida.Confirmar);
        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().ContainSingle();
    }

    private static bool OfreceLoHeRevisado(IRenderedComponent<Bandeja> cut) =>
        cut.FindAll(".rs-sinrevisar button").Any(b => b.TextContent.Trim() == "Lo he revisado");

    /// <summary>
    /// Revisión puente de la r3: la sugerencia insertada en A cuenta como mostrada solo en esa apertura. Tras ir a B y volver
    /// a A, el redactor ya no la tiene y «Lo he revisado» deja de ofrecerse hasta ponerla otra vez.
    /// </summary>
    [Fact]
    public async Task La_sugerencia_insertada_deja_de_contar_como_mostrada_al_ir_a_otra_conversacion_y_volver()
    {
        var (escenario, _, segunda, _) = DosConversacionesConSugerenciaEnLaPrimera();
        escenario.Interceptar = p => p is ObtenerRespuestaSugeridaQuery q && q.ConversacionId == segunda.Id ? Ya(null) : null;
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-bloque").Should().ContainSingle());
        await Boton(cut, ".rs-bloque button", "Insertar para revisar").ClickAsync(new MouseEventArgs());
        OfreceLoHeRevisado(cut).Should().BeTrue("se acaba de insertar en esta apertura");

        var apertura = SeleccionarFila(cut, "Rechazo del portal");
        cut.WaitForAssertion(() => cut.FindAll(".modal-pie button").Should().Contain(b => b.TextContent.Trim() == "Salir y descartar"));
        await cut.PulsarEnElAvisoAsync("Salir y descartar");
        await apertura;
        cut.WaitForAssertion(() => cut.Find(".bandeja-centro-titulo-fila h2").TextContent.Trim().Should().Be("Rechazo del portal"));
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-sinrevisar").Should().ContainSingle());

        OfreceLoHeRevisado(cut).Should().BeFalse("es otra apertura: aquí la sugerencia no está en el redactor");
        cut.Find(".rs-sinrevisar").GetAttribute("data-en-redactor").Should().Be("no");
    }

    /// <summary>
    /// Revisión puente de la r3: una recarga del mismo hilo (mensaje entrante) no es otra apertura; la sugerencia insertada
    /// sigue en el redactor y se puede revisar.
    /// </summary>
    [Fact]
    public async Task Una_recarga_del_mismo_hilo_mantiene_la_sugerencia_insertada_como_mostrada()
    {
        var (escenario, primera, _, _) = DosConversacionesConSugerenciaEnLaPrimera();
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-bloque").Should().ContainSingle());
        await Boton(cut, ".rs-bloque button", "Insertar para revisar").ClickAsync(new MouseEventArgs());

        var lecturas = mediador.Enviados.OfType<ObtenerConversacionPorIdQuery>().Count();
        await cut.InvokeAsync(() => (Task)typeof(Bandeja).GetMethod("AlRecibirMensajeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cut.Instance, [new MensajeWhatsAppRecibidoEvent(Guid.NewGuid(), primera.Id)])!);
        cut.WaitForAssertion(() => mediador.Enviados.OfType<ObtenerConversacionPorIdQuery>().Count().Should().BeGreaterThan(lecturas));

        cut.WaitForAssertion(() => OfreceLoHeRevisado(cut).Should().BeTrue("la recarga no cambia la apertura"));
        TextoDelRedactor(cut).Should().Be(TextoSugerido);
    }

    /// <summary>
    /// Revisión puente de la r3: si se vacía el redactor tras insertar, lo que se escriba después ya no sale de la sugerencia
    /// y no la confirma al enviar.
    /// </summary>
    [Fact]
    public async Task Vaciar_el_redactor_tras_insertar_impide_que_una_respuesta_nueva_confirme_la_sugerencia()
    {
        var (escenario, _) = EscenarioConSugerencia();
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-bloque").Should().ContainSingle());
        await Boton(cut, ".rs-bloque button", "Insertar para revisar").ClickAsync(new MouseEventArgs());

        await Escribir(cut, string.Empty);
        await Escribir(cut, "Hola Carmen, recibido todo. Lo miramos esta tarde y mañana le decimos algo sin falta.");
        if (!EnviarDeshabilitado(cut)) await Enviar(cut);

        escenario.AccionesSobreSugerencia.Should().Equal(AccionRespuestaSugerida.Insertar);
        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().BeEmpty();
        OfreceLoHeRevisado(cut).Should().BeFalse("al vaciar el redactor la sugerencia dejó de estar en él");
    }

    /// <summary>
    /// MEDIO (r3): «Insertar» tarda y entretanto se escribe en el redactor de la misma apertura. La decisión de sustituir se
    /// tomó con el redactor vacío: lo escrito después no se pisa, y la sugerencia no cuenta como mostrada en este redactor.
    /// </summary>
    [Fact]
    public async Task La_insercion_que_tarda_no_pisa_lo_escrito_mientras_esperaba()
    {
        var (escenario, _) = EscenarioConSugerencia();
        var insercion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p is ResolverRespuestaSugeridaCommand { Accion: AccionRespuestaSugerida.Insertar }
            ? insercion.Task.ContinueWith(t => { escenario.Sugerencia = escenario.Sugerencia! with { Estado = EstadoRespuestaSugerida.SinRevisar }; return t.Result; })
            : null;
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-bloque").Should().ContainSingle());

        var clic = Boton(cut, ".rs-bloque button", "Insertar para revisar").ClickAsync(new MouseEventArgs());
        const string escritoMientras = "Carmen, esto lo he escrito mientras la sugerencia se insertaba.";
        await Escribir(cut, escritoMientras);
        await cut.InvokeAsync(() => insercion.SetResult(Result.Exito()));
        await clic;

        TextoDelRedactor(cut).Should().Be(escritoMientras, "se decidió sustituir un redactor vacío, no lo escrito después");
        cut.WaitForAssertion(() => cut.FindAll(".rs-sinrevisar").Should().ContainSingle());
        cut.FindAll(".rs-sinrevisar button").Should().NotContain(b => b.TextContent.Trim() == "Lo he revisado",
            "la sugerencia no llegó a este redactor");
        EnviarDeshabilitado(cut).Should().BeTrue();
    }

    /// <summary>
    /// BAJO (r3): la comprobación de la conversación abierta, por sí sola. En la interfaz, cambiar de conversación cambia
    /// también la apertura, así que en los recorridos de los tests anteriores el token ya rechaza la devolución y la
    /// comprobación de la conversación queda tapada. Aquí el aviso lleva la apertura vigente, la de B, y la sugerencia de A:
    /// solo la conversación seleccionada impide que el texto de A acabe en el redactor de B.
    /// </summary>
    [Fact]
    public async Task La_insercion_con_la_apertura_vigente_pero_de_otra_conversacion_no_escribe_en_el_redactor()
    {
        var (escenario, _, segunda, detallePrimera) = DosConversacionesConSugerenciaEnLaPrimera();
        escenario.Interceptar = p => p is ObtenerRespuestaSugeridaQuery q && q.ConversacionId == segunda.Id ? Ya(null) : null;
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Rechazo del portal");
        cut.WaitForAssertion(() => cut.Find(".bandeja-centro-titulo-fila h2").TextContent.Trim().Should().Be("Rechazo del portal"));

        var aperturaVigente = (int)typeof(Bandeja).GetField("_aperturaVigente", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cut.Instance)!;
        var avisoDeA = new RespuestaSugeridaPanel.SugerenciaDeLaSeleccion(PropuestaPara(detallePrimera), aperturaVigente);
        await cut.InvokeAsync(() => (Task)typeof(Bandeja).GetMethod("InsertarSugerenciaAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cut.Instance, [avisoDeA])!);

        // El campo y no el ComposerBar: invocar el método no repinta, y leer el parámetro del redactor daría vacío siempre.
        typeof(Bandeja).GetField("_textoRespuesta", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cut.Instance)
            .Should().Be(string.Empty, "la sugerencia es de A, aunque el aviso lleve la apertura vigente");
    }

    /// <summary>
    /// MEDIO 2 (r2): mientras se guarda la nota fija el área es de solo lectura; y si aun así llega texto nuevo durante el
    /// guardado, al terminar no se pierde: la edición sigue abierta con él y cuenta como cambio sin guardar.
    /// </summary>
    [Fact]
    public async Task Lo_escrito_en_la_nota_fija_mientras_se_guarda_no_se_pierde()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion), NotaContacto = NotaDeCarmen() };
        escenario.Conversaciones.Add(conversacion);
        var puerta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<object?> Retener(FijarNotaContactoAgendaCommand comando)
        {
            await puerta.Task;
            escenario.NotasFijadas.Add(comando);
            return Result.Exito();
        }
        escenario.Interceptar = p => p is FijarNotaContactoAgendaCommand c ? Retener(c) : null;
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".nota-fija").Should().ContainSingle());

        await Boton(cut, ".nota-fija button", "Editar").ClickAsync(new MouseEventArgs());
        await cut.Find(".nota-fija-area").InputAsync(new ChangeEventArgs { Value = "Llamar por la tarde." });
        var guardado = Boton(cut, ".nota-fija button", "Guardar nota fija").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find(".nota-fija-area").HasAttribute("readonly").Should().BeTrue("mientras se guarda no se escribe"));

        await cut.Find(".nota-fija-area").InputAsync(new ChangeEventArgs { Value = "Llamar por la tarde, después de las cuatro." });
        await cut.InvokeAsync(() => puerta.SetResult());
        await guardado;

        escenario.NotasFijadas.Should().ContainSingle().Which.Texto.Should().Be("Llamar por la tarde.");
        var area = cut.Find(".nota-fija-area");
        area.GetAttribute("value").Should().Be("Llamar por la tarde, después de las cuatro.", "lo escrito después sigue en el área");
        area.HasAttribute("readonly").Should().BeFalse();
        cut.FindComponent<NotaFijaContacto>().Instance.TieneTextoSinGuardar.Should().BeTrue();
    }

    private static NotaContactoDto NotaDeLaura() =>
        new(Guid.NewGuid(), "Laura Gil", "Solo por correo, nunca por teléfono.", null, new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc));

    /// <summary>
    /// MEDIO 4: al abrir otra conversación de la misma Agenda, la nota fija del contacto anterior no sigue en pantalla, y por
    /// tanto no se puede editar ni guardar, mientras carga la del contacto nuevo; lo que se guarda después es del nuevo.
    /// </summary>
    [Fact]
    public async Task La_nota_fija_del_contacto_anterior_no_se_puede_editar_mientras_carga_la_del_nuevo()
    {
        var (escenario, _, _) = DosConversacionesDeLaMismaAgenda();
        var carmen = NotaDeCarmen();
        var laura = NotaDeLaura();
        var notaDeLaura = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p switch
        {
            ObtenerNotaContactoQuery { Email: "carmen.ruiz@refrielectric.es" } => Ya(carmen),
            ObtenerNotaContactoQuery { Email: "laura.gil@refrielectric.es" } => notaDeLaura.Task,
            _ => null
        };
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.Find(".nota-fija").TextContent.Should().Contain("Carmen Ruiz"));

        var apertura = SeleccionarFila(cut, "Rechazo del portal");
        cut.WaitForAssertion(() => cut.Find(".bandeja-centro-titulo-fila h2").TextContent.Trim().Should().Be("Rechazo del portal"));

        cut.FindAll(".nota-fija").Should().BeEmpty("la nota de Carmen no es la de este contacto, y la de Laura aún no ha llegado");
        await cut.InvokeAsync(() => notaDeLaura.SetResult(laura));
        await apertura;
        cut.WaitForAssertion(() => cut.Find(".nota-fija").TextContent.Should().Contain("Laura Gil"));

        await Boton(cut, ".nota-fija button", "Editar").ClickAsync(new MouseEventArgs());
        await cut.Find(".nota-fija-area").InputAsync(new ChangeEventArgs { Value = "Solo por correo." });
        await Boton(cut, ".nota-fija button", "Guardar nota fija").ClickAsync(new MouseEventArgs());
        escenario.NotasFijadas.Should().ContainSingle().Which.ContactoId.Should().Be(laura.ContactoId);
    }

    /// <summary>
    /// MEDIO (r3): al pulsar otra conversación, la selección cambia antes de que llegue su detalle, y hasta entonces el detalle
    /// en memoria es el de la anterior. Mientras carga el de B, el panel de contexto de A (su nota fija y sus notas del
    /// equipo) no está en pantalla: no se puede editar ni guardar la nota de Carmen bajo la selección de B.
    /// </summary>
    [Fact]
    public async Task Mientras_carga_el_detalle_de_otra_conversacion_no_se_puede_editar_la_nota_fija_de_la_anterior()
    {
        var (escenario, _, segunda) = DosConversacionesDeLaMismaAgenda();
        escenario.NotasInternas.Add(new NotaInternaDetalleDto(Guid.NewGuid(), Guid.NewGuid(), "Pedir el TC2 antes del viernes.", DateTime.UtcNow));
        var laura = NotaDeLaura();
        var detalleSegunda = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p switch
        {
            ObtenerConversacionPorIdQuery q when q.Id == segunda.Id => detalleSegunda.Task,
            ObtenerNotaContactoQuery { Email: "carmen.ruiz@refrielectric.es" } => Ya(NotaDeCarmen()),
            ObtenerNotaContactoQuery { Email: "laura.gil@refrielectric.es" } => Ya(laura),
            _ => null
        };
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.Find(".nota-fija").TextContent.Should().Contain("Carmen Ruiz"));
        cut.FindAll(".notas-equipo").Should().ContainSingle();

        var apertura = SeleccionarFila(cut, "Rechazo del portal");

        cut.WaitForAssertion(() => cut.FindAll(".bandeja-centro-titulo-fila h2").Should().BeEmpty("el detalle de B aún carga"));
        cut.FindAll(".nota-fija").Should().BeEmpty("la nota fija de Carmen es de la conversación anterior");
        cut.FindAll(".notas-equipo").Should().BeEmpty("las notas del equipo de A no son las de B");
        cut.FindAll(".bandeja-cliente").Should().BeEmpty("el panel de contexto de A no se pinta bajo la selección de B");

        await cut.InvokeAsync(() => detalleSegunda.SetResult(escenario.Detalle(segunda.Id)));
        await apertura;
        cut.WaitForAssertion(() => cut.Find(".nota-fija").TextContent.Should().Contain("Laura Gil"));

        await Boton(cut, ".nota-fija button", "Editar").ClickAsync(new MouseEventArgs());
        await cut.Find(".nota-fija-area").InputAsync(new ChangeEventArgs { Value = "Solo por correo." });
        await Boton(cut, ".nota-fija button", "Guardar nota fija").ClickAsync(new MouseEventArgs());
        escenario.NotasFijadas.Should().ContainSingle().Which.ContactoId.Should().Be(laura.ContactoId);
    }

    /// <summary>
    /// Revisión puente de la PR de E21 (MEDIO): con la nota fija de Carmen en edición, llega un mensaje nuevo de Laura al
    /// mismo hilo y la página recarga el detalle. El último entrante ya es de Laura, pero la edición de Carmen no se vacía
    /// en silencio: el interlocutor no cambia mientras se edita, y lo que se guarda es de Carmen.
    /// </summary>
    [Fact]
    public async Task Un_mensaje_nuevo_de_otro_participante_no_vacia_la_nota_fija_en_edicion()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var deLaura = Conversacion("Documentación pendiente", ClienteRefrielectric, "laura.gil@refrielectric.es");
        var detalle = DetalleDe(conversacion);
        var escenario = new Escenario { Detalle = _ => detalle };
        escenario.Conversaciones.Add(conversacion);
        var carmen = NotaDeCarmen();
        escenario.Interceptar = p => p switch
        {
            ObtenerNotaContactoQuery { Email: "carmen.ruiz@refrielectric.es" } => Ya(carmen),
            ObtenerNotaContactoQuery { Email: "laura.gil@refrielectric.es" } => Ya(NotaDeLaura()),
            _ => null
        };
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.Find(".nota-fija").TextContent.Should().Contain("Carmen Ruiz"));
        await Boton(cut, ".nota-fija button", "Editar").ClickAsync(new MouseEventArgs());
        await cut.Find(".nota-fija-area").InputAsync(new ChangeEventArgs { Value = "Llamar por la tarde." });

        var mensajeDeLaura = DetalleDe(deLaura).Mensajes[0] with { FechaUtc = DateTime.UtcNow.AddMinutes(5) };
        detalle = detalle with { Id = conversacion.Id, Mensajes = [.. detalle.Mensajes, mensajeDeLaura] };
        await cut.InvokeAsync(() => (Task)typeof(Bandeja).GetMethod("AlRecibirMensajeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cut.Instance, [new MensajeWhatsAppRecibidoEvent(Guid.NewGuid(), conversacion.Id)])!);

        cut.WaitForAssertion(() => cut.Find(".nota-fija-area").GetAttribute("value").Should().Be("Llamar por la tarde.",
            "la edición en curso no se vacía porque haya escrito otro participante"));
        cut.FindComponent<NotaFijaContacto>().Instance.TieneTextoSinGuardar.Should().BeTrue();
        await Boton(cut, ".nota-fija button", "Guardar nota fija").ClickAsync(new MouseEventArgs());
        escenario.NotasFijadas.Should().ContainSingle().Which.ContactoId.Should().Be(carmen.ContactoId);
    }

    /// <summary>
    /// Revisión Codex r1 de la #2167, MEDIO 2: con la nota fija en edición, la recarga del mismo hilo (un mensaje nuevo) falla.
    /// El detalle y el borrador se conservan, el error se avisa y lo escrito se puede guardar; vaciar el detalle desmontaba el
    /// panel y destruía el borrador sin pasar por la confirmación de abandono.
    /// </summary>
    [Fact]
    public async Task Una_recarga_fallida_del_hilo_no_destruye_el_borrador_de_la_nota_fija()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion), NotaContacto = NotaDeCarmen() };
        escenario.Conversaciones.Add(conversacion);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".nota-fija").Should().ContainSingle());
        await Boton(cut, ".nota-fija button", "Editar").ClickAsync(new MouseEventArgs());
        await cut.Find(".nota-fija-area").InputAsync(new ChangeEventArgs { Value = "Llamar por la tarde." });

        escenario.Interceptar = p => p is ObtenerConversacionPorIdQuery
            ? Task.FromException<object?>(new InvalidOperationException("boom"))
            : null;
        await cut.InvokeAsync(() => (Task)typeof(Bandeja).GetMethod("AlRecibirMensajeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cut.Instance, [new MensajeWhatsAppRecibidoEvent(Guid.NewGuid(), conversacion.Id)])!);

        cut.WaitForAssertion(() => Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(
            m => m.Mensaje.StartsWith("No pudimos abrir la conversación"), "el fallo de la recarga se avisa"));
        cut.Find(".nota-fija-area").GetAttribute("value").Should().Be("Llamar por la tarde.", "el borrador sobrevive a la recarga fallida");
        cut.FindComponent<NotaFijaContacto>().Instance.TieneTextoSinGuardar.Should().BeTrue();
        cut.Find(".bandeja-centro-titulo-fila h2").TextContent.Trim().Should().Be("Documentación pendiente", "el hilo abierto sigue en pantalla");

        escenario.Interceptar = _ => null;
        await Boton(cut, ".nota-fija button", "Guardar nota fija").ClickAsync(new MouseEventArgs());
        escenario.NotasFijadas.Should().ContainSingle().Which.Texto.Should().Be("Llamar por la tarde.");
    }

    /// <summary>
    /// Revisión Codex r1 de la #2167, BAJO 4: vaciar el campo de una nota existente la retira (el comando define el texto vacío
    /// como retirada); sobre una nota que no existe, no hay nada que guardar.
    /// </summary>
    [Fact]
    public async Task Vaciar_el_campo_de_la_nota_fija_la_retira()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion), NotaContacto = NotaDeCarmen() };
        escenario.Conversaciones.Add(conversacion);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".nota-fija").Should().ContainSingle());

        await Boton(cut, ".nota-fija button", "Editar").ClickAsync(new MouseEventArgs());
        await cut.Find(".nota-fija-area").InputAsync(new ChangeEventArgs { Value = "   " });
        var retirar = Boton(cut, ".nota-fija button", "Retirar nota fija");
        retirar.HasAttribute("disabled").Should().BeFalse("con el campo vacío, la acción sobre una nota existente es retirarla");
        await retirar.ClickAsync(new MouseEventArgs());

        escenario.NotasFijadas.Should().ContainSingle().Which.Texto.Should().BeEmpty();
        cut.FindAll(".nota-fija-area").Should().BeEmpty("al retirar se sale de la edición");
        cut.Find(".nota-fija").TextContent.Should().NotContain("Prefiere que la llamen por la mañana.").And.Contain("no tiene nota fija");
    }

    [Fact]
    public async Task Sobre_una_nota_fija_inexistente_el_campo_vacio_no_ofrece_guardar_nada()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion), NotaContacto = NotaDeCarmen(texto: null) };
        escenario.Conversaciones.Add(conversacion);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".nota-fija").Should().ContainSingle());

        await Boton(cut, ".nota-fija button", "Añadir nota fija").ClickAsync(new MouseEventArgs());

        Boton(cut, ".nota-fija button", "Guardar nota fija").HasAttribute("disabled").Should().BeTrue();
    }

    /// <summary>
    /// Revisión Codex r1 de la #2167, BAJO 5: llega un mensaje de otro participante mientras se edita la nota fija; la edición
    /// no se vacía. Al terminarla (aquí, cancelándola) el componente avisa a la página, que recalcula el interlocutor: la
    /// nota que se ve es la de Laura, sin esperar a otro render de la Bandeja.
    /// </summary>
    [Fact]
    public async Task Al_terminar_la_edicion_la_nota_fija_pasa_al_interlocutor_del_ultimo_mensaje()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var deLaura = Conversacion("Documentación pendiente", ClienteRefrielectric, "laura.gil@refrielectric.es");
        var detalle = DetalleDe(conversacion);
        var escenario = new Escenario { Detalle = _ => detalle };
        escenario.Conversaciones.Add(conversacion);
        escenario.Interceptar = p => p switch
        {
            ObtenerNotaContactoQuery { Email: "carmen.ruiz@refrielectric.es" } => Ya(NotaDeCarmen()),
            ObtenerNotaContactoQuery { Email: "laura.gil@refrielectric.es" } => Ya(NotaDeLaura()),
            _ => null
        };
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.Find(".nota-fija").TextContent.Should().Contain("Carmen Ruiz"));
        await Boton(cut, ".nota-fija button", "Editar").ClickAsync(new MouseEventArgs());

        var mensajeDeLaura = DetalleDe(deLaura).Mensajes[0] with { FechaUtc = DateTime.UtcNow.AddMinutes(5) };
        detalle = detalle with { Id = conversacion.Id, Mensajes = [.. detalle.Mensajes, mensajeDeLaura] };
        await cut.InvokeAsync(() => (Task)typeof(Bandeja).GetMethod("AlRecibirMensajeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cut.Instance, [new MensajeWhatsAppRecibidoEvent(Guid.NewGuid(), conversacion.Id)])!);
        cut.Find(".nota-fija").TextContent.Should().Contain("Carmen Ruiz", "mientras se edita, el interlocutor no cambia");

        await Boton(cut, ".nota-fija button", "Cancelar").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find(".nota-fija").TextContent.Should().Contain("Laura Gil"));
    }
    /// <summary>MEDIO 7: una nota fija editada y sin guardar cuenta como cambio sin guardar al abrir otra conversación.</summary>
    [Fact]
    public async Task Abrir_otra_conversacion_con_la_nota_fija_a_medias_pregunta_antes_de_descartarla()
    {
        var (escenario, _, _) = DosConversacionesDeLaMismaAgenda();
        escenario.NotaContacto = NotaDeCarmen();
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".nota-fija").Should().ContainSingle());
        await Boton(cut, ".nota-fija button", "Editar").ClickAsync(new MouseEventArgs());
        await cut.Find(".nota-fija-area").InputAsync(new ChangeEventArgs { Value = "Llamar por la tarde." });

        var apertura = SeleccionarFila(cut, "Rechazo del portal");

        cut.WaitForAssertion(() => cut.FindAll(".modal-pie button").Should().Contain(b => b.TextContent.Trim() == "Salir y descartar",
            "la edición de la nota fija se perdería sin aviso"));
        cut.Find(".bandeja-centro-titulo-fila h2").TextContent.Trim().Should().Be("Documentación pendiente");
        await cut.PulsarEnElAvisoAsync("Salir y descartar");
        await apertura;
        cut.WaitForAssertion(() => cut.Find(".bandeja-centro-titulo-fila h2").TextContent.Trim().Should().Be("Rechazo del portal"));
        escenario.NotasFijadas.Should().BeEmpty();
    }

    /// <summary>
    /// MEDIO 6: una conversación anclada a una Empresa, sin Cliente empresarial activo ni acciones pendientes, también enseña
    /// la nota fija del contacto y las notas del equipo.
    /// </summary>
    [Fact]
    public async Task Una_conversacion_anclada_a_una_Empresa_sin_Cliente_ni_acciones_muestra_las_notas()
    {
        var conversacion = Conversacion("Documentación de la subcontrata", remitente: "laura.gil@aislamientos.es");
        var participante = new ParticipanteDetalleDto(
            Guid.NewGuid(), "laura.gil@aislamientos.es", RolParticipante.De, TipoParticipanteOrigen.Empresa, Guid.NewGuid());
        var detalle = DetalleDe(conversacion) with { Participantes = [participante] };
        var escenario = new Escenario { Detalle = _ => detalle, NotaContacto = NotaDeLaura() };
        escenario.NotasInternas.Add(new NotaInternaDetalleDto(Guid.NewGuid(), Guid.NewGuid(), "Pedir el TC2 antes del viernes.", DateTime.UtcNow));
        escenario.Conversaciones.Add(conversacion);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación de la subcontrata");

        cut.WaitForAssertion(() => cut.FindAll(".nota-fija").Should().ContainSingle());
        cut.FindAll(".notas-equipo").Should().ContainSingle();
        cut.FindAll(".bandeja-cliente-identidad").Should().BeEmpty("no hay Cliente empresarial activo");
    }

    /// <summary>
    /// MEDIO 8: en correo la sugerencia se guarda como HTML, y el redactor es un campo de texto: recibe el texto, sin
    /// etiquetas ni entidades, y la revisión lo mide como texto (sigue sin revisar y bloqueando Enviar).
    /// </summary>
    [Fact]
    public async Task Insertar_una_sugerencia_de_correo_pone_texto_y_no_HTML_en_el_redactor()
    {
        var (escenario, detalle) = EscenarioConSugerencia();
        escenario.Sugerencia = PropuestaPara(detalle,
            "<p>Buenos días, Carmen.</p><p>Recibimos la documentación &amp; la revisaremos hoy mismo; le confirmamos mañana.</p>");
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".rs-bloque").Should().ContainSingle());

        await Boton(cut, ".rs-bloque button", "Insertar para revisar").ClickAsync(new MouseEventArgs());

        TextoDelRedactor(cut).Should().Be(
            "Buenos días, Carmen.\n\nRecibimos la documentación & la revisaremos hoy mismo; le confirmamos mañana.");
        EnviarDeshabilitado(cut).Should().BeTrue("el texto es la sugerencia sin tocar: la distancia se mide sin marcado");
    }

    // ---------------------------------------------------------------- mockup Gen 2

    [Fact]
    public void El_titulo_el_buscador_y_Nuevo_correo_viven_en_la_cabecera_de_la_pagina()
    {
        var (cut, _) = Renderizar(new Escenario());

        var cabecera = cut.Find(".bandeja-cabecera-pagina");
        cabecera.QuerySelector("h1")!.TextContent.Trim().Should().Be("Comunicaciones");
        cabecera.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Nuevo correo")
            .And.NotContain("Redactar");
        var buscador = cabecera.QuerySelector("input")!;
        buscador.GetAttribute("placeholder").Should().Be("Buscar en el asunto y en el texto de los mensajes");

        cut.FindAll(".bandeja-lista-cabecera").Should().BeEmpty("el título y la acción subieron a la cabecera de la página");
        cut.FindAll(".bandeja-toolbar").Should().BeEmpty(
            "el mockup Gen 2 no tiene banda de cabecera sobre las tres columnas");
    }

    [Fact]
    public async Task El_ambito_es_una_navegacion_de_dos_items_y_cambiar_a_Mi_buzon_pide_el_buzon_personal()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.Add(Conversacion("Documentación pendiente", ClienteRefrielectric));
        var (cut, mediador) = Renderizar(escenario);

        var items = cut.FindAll("nav.bandeja-nav .bandeja-nav-item");
        items.Select(p => p.TextContent.Trim()).Should().Equal(["Clientes", "Mi buzón personal"],
            "decisión de rótulo del 2026-10-09: la contraparte de una Relación Empresarial, el Cliente empresarial, se rotula «Clientes»");
        items[0].GetAttribute("aria-current").Should().Be("true");
        items[1].GetAttribute("aria-current").Should().BeNull();

        await items[1].ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<ObtenerMensajesBuzonPersonalQuery>().Should().ContainSingle(
            "el ámbito vive dentro de la bandeja: cambia de ámbito, no navega a otra pantalla");
        var tras = cut.FindAll("nav.bandeja-nav .bandeja-nav-item");
        tras[1].GetAttribute("aria-current").Should().Be("true");
        tras[0].GetAttribute("aria-current").Should().BeNull();
        cut.FindAll(".bandeja-filtros-form").Should().BeEmpty("los filtros de conversaciones no aplican al buzón personal");
        cut.FindAll("[role=tablist]").Should().BeEmpty("el ámbito dejó de ser un tablist: es la navegación del lateral de la maqueta");
    }

    [Fact]
    public async Task La_bandeja_y_el_hilo_muestran_la_misma_lista_plana_sin_agrupar_por_Empresa()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.AddRange([
            Conversacion("Documentos", ClienteRefrielectric),
            Conversacion("RNT", ClienteRefrielectric),
            Conversacion("Rechazo", remitente: "notificaciones@example.invalid")]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        var (cut, _) = Renderizar(escenario);
        AsuntosVisibles(cut).Should().Equal("Documentos", "RNT", "Rechazo");
        cut.FindAll(".bandeja-fila").Should().BeEmpty();
        await SeleccionarFila(cut, "Documentos");
        AsuntosVisibles(cut).Should().Equal("Documentos", "RNT", "Rechazo");
        cut.FindAll(".bandeja-grupo-cabecera").Should().BeEmpty();
    }

    [Fact]
    public async Task La_fila_del_hilo_pone_el_remitente_y_el_asunto_en_lineas_propias()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.Add(Conversacion("Documentación pendiente", ClienteRefrielectric));
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentaci\u00f3n pendiente");

        var fila = cut.Find(".bandeja-fila");
        fila.QuerySelector(".bandeja-fila-linea1 .bandeja-fila-remitente")!.TextContent.Trim().Should().Be("Refrielectric S.A.");
        fila.QuerySelector(".bandeja-fila-linea2 .bandeja-fila-asunto")!.TextContent.Trim().Should().Be("Documentación pendiente");
        fila.QuerySelector(".bandeja-fila-separador").Should().BeNull("remitente y asunto ya no comparten línea");

    }

    [Fact]
    public void Sin_ninguna_conversacion_el_vacio_no_culpa_a_unos_filtros_que_nadie_puso()
    {
        var (cut, _) = Renderizar(new Escenario());

        cut.Find(".bandeja-centro .estado-vacio h3").TextContent.Trim().Should().Be("No quedan conversaciones");
        cut.Markup.Should().NotContain("Sin resultados");
        cut.FindAll(".bandeja-centro .estado-vacio button").Should().BeEmpty(
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
        cut.Find(".bandeja-centro .estado-vacio h3").TextContent.Trim()
            .Should().Be("Sin resultados");

        await Boton(cut, ".bandeja-centro .estado-vacio button", "Quitar los filtros").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().NotContain("estado=", "un filtro quitado no puede seguir en la URL que se comparte");
        mediador.Enviados.OfType<ObtenerConversacionesQuery>().Last().Estado.Should().BeNull();
        AsuntosVisibles(cut).Should().Equal(["Documentación pendiente"]);
    }

    [Fact]
    public void Sin_conversaciones_ni_filtros_la_bandeja_dice_Al_dia()
    {
        var (cut, _) = Renderizar(new Escenario());
        cut.Find(".bandeja-centro .bandeja-al-dia").TextContent.Trim().Should().Be("Al día");

        // Maqueta (`.vacio`, E14): el distintivo y el estado vacío van en un mismo bloque y sin icono.
        var bloque = cut.Find(".bandeja-centro .bandeja-vacio-bloque");
        bloque.QuerySelector(".bandeja-al-dia").Should().NotBeNull();
        bloque.QuerySelector(".estado-vacio").Should().NotBeNull();
        cut.FindAll(".bandeja-centro .estado-vacio-icono").Should().BeEmpty("el vacío de la maqueta no lleva icono");
    }

    [Fact]
    public void Un_vacio_por_filtro_no_dice_Al_dia()
    {
        var (cut, _) = Renderizar(new Escenario(), "comunicaciones?estado=Resuelta");
        cut.FindAll(".bandeja-al-dia").Should().BeEmpty("un vacío por filtro no es estar al día");
        cut.Find(".bandeja-centro .estado-vacio h3").TextContent.Trim().Should().Be("Sin resultados");
    }

    [Fact]
    public void Mientras_carga_la_lista_Cargando_es_un_estado_anunciado_a_los_lectores_de_pantalla()
    {
        var escenario = new Escenario();
        var pendiente = new TaskCompletionSource<object?>();
        escenario.Interceptar = p => p is ObtenerConversacionesQuery ? pendiente.Task : null;
        var (cut, _) = Renderizar(escenario);

        var texto = cut.Find(".bandeja-centro .bandeja-cargando-texto");
        texto.TextContent.Trim().Should().Be("Cargando…");
        texto.GetAttribute("role").Should().Be("status", "sin role=status la carga no se anuncia a un lector de pantalla");
    }

    [Fact]
    public async Task Si_la_lista_falla_dice_No_pudimos_cargar_la_lista_y_Reintentar_la_vuelve_a_pedir()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.Add(Conversacion("Documentación pendiente", ClienteRefrielectric));
        var falla = true;
        escenario.Interceptar = p => falla && p is ObtenerConversacionesQuery
            ? Task.FromException<object?>(new InvalidOperationException("boom"))
            : null;
        var (cut, _) = Renderizar(escenario);

        cut.Find(".bandeja-centro .estado-vacio h3").TextContent.Trim().Should().Be("No pudimos cargar la lista");
        falla = false;
        await Boton(cut, ".bandeja-centro .estado-vacio button", "Reintentar").ClickAsync(new MouseEventArgs());

        AsuntosVisibles(cut).Should().Equal(["Documentación pendiente"]);
    }

    [Fact]
    public async Task El_hilo_tiene_encabezado_de_lista_con_titulo_recuento_y_vuelta_a_la_tabla()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.Add(Conversacion("Documentación pendiente", ClienteRefrielectric));
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");

        var cabecera = cut.Find(".bandeja-lista .bandeja-lis-cab");
        cabecera.QuerySelector("h2")!.TextContent.Trim().Should().Be("Todas");
        cabecera.QuerySelector(".bandeja-mini")!.TextContent.Trim().Should().Be("Tabla");

        await cabecera.QuerySelector(".bandeja-mini")!.ClickAsync(new MouseEventArgs());

        cut.FindAll(".bandeja-modo-tabla").Should().NotBeEmpty("«Tabla» devuelve a la bandeja inicial");
    }

    [Fact]
    public async Task Si_abrir_la_conversacion_falla_no_se_pinta_el_hilo_anterior_y_Reintentar_la_abre()
    {
        var escenario = new Escenario();
        var primera = Conversacion("Primera conversación", ClienteRefrielectric);
        var segunda = Conversacion("Segunda conversación", ClienteRefrielectric);
        escenario.Conversaciones.AddRange([primera, segunda]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");
        cut.FindAll(".bandeja-cliente").Should().NotBeEmpty("control positivo: con la primera abierta hay columna de contexto");

        escenario.Interceptar = p => p is ObtenerConversacionPorIdQuery q && q.Id == segunda.Id
            ? Task.FromException<object?>(new InvalidOperationException("boom"))
            : null;
        await SeleccionarFila(cut, "Segunda conversación");

        cut.Find(".bandeja-centro .estado-vacio h3").TextContent.Trim().Should().Be("No pudimos abrir la conversación");
        cut.Find(".bandeja-centro").TextContent.Should().NotContain("Primera conversación",
            "el hilo anterior no puede quedar bajo la selección nueva");
        cut.FindAll(".bandeja-cliente").Should().BeEmpty("la columna de contexto y de acciones tampoco puede operar sobre la conversación anterior");

        escenario.Interceptar = _ => null;
        await Boton(cut, ".bandeja-centro .estado-vacio button", "Reintentar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".bandeja-centro .estado-vacio").Should().BeEmpty();
        cut.Find(".bandeja-centro").TextContent.Should().Contain("Segunda conversación");
    }

    /// <summary>
    /// Revisión puente del delta r1 de la #2167 (MEDIO): al abrir otra conversación, el detalle nuevo se asigna antes de las
    /// consultas de su Cliente empresarial. Si una de ellas falla, el contexto de la conversación anterior no puede quedar bajo
    /// el hilo nuevo (la ficha, los centros y los selectores serían de otra): solo una recarga del mismo hilo conserva su contexto.
    /// </summary>
    [Fact]
    public async Task Si_falla_el_contexto_del_Cliente_de_la_conversacion_nueva_no_queda_el_de_la_anterior()
    {
        var escenario = new Escenario();
        var primera = Conversacion("Primera conversación", ClienteRefrielectric);
        var segunda = Conversacion("Segunda conversación", ClienteEbro);
        escenario.Conversaciones.AddRange([primera, segunda]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");
        cut.FindAll(".bandeja-cliente").Should().NotBeEmpty("control positivo: con la primera abierta hay columna de contexto");

        escenario.Interceptar = p => p is ObtenerClientePorIdQuery q && q.Id == ClienteEbro.Id
            ? Task.FromException<object?>(new InvalidOperationException("boom"))
            : null;
        await SeleccionarFila(cut, "Segunda conversación");

        cut.Find(".bandeja-centro .estado-vacio h3").TextContent.Trim().Should().Be("No pudimos abrir la conversación");
        cut.FindAll(".bandeja-cliente").Should().BeEmpty("el contexto del Cliente empresarial de la primera no puede quedar bajo la segunda");
    }

    /// <summary>
    /// Revisión Codex r2 de la #2167, MEDIO 1: <c>CargarDetalleAsync</c> asigna el detalle de la conversación nueva antes de esperar
    /// el contexto de su Cliente empresarial. Con esa espera en vuelo, la columna de contexto no puede pintar la ficha ni los
    /// selectores del Cliente empresarial de la conversación anterior; al llegar, se pinta la del nuevo. Detecta volver a decidir
    /// el pintado solo por «el detalle es el de la selección».
    /// </summary>
    [Fact]
    public async Task Mientras_carga_el_contexto_de_la_conversacion_nueva_no_se_pinta_el_del_Cliente_de_la_anterior()
    {
        var escenario = new Escenario();
        var primera = Conversacion("Primera conversación", ClienteRefrielectric);
        var segunda = Conversacion("Segunda conversación", ClienteEbro);
        escenario.Conversaciones.AddRange([primera, segunda]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");
        cut.Find(".bandeja-cliente").TextContent.Should().Contain(ClienteRefrielectric.RazonSocial, "control positivo: con la primera abierta se ve su ficha");

        var clienteDeLaSegunda = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consultas = 0;
        escenario.Interceptar = p => p is ObtenerClientePorIdQuery q && q.Id == ClienteEbro.Id ? Retener(clienteDeLaSegunda.Task) : null;
        Task<object?> Retener(Task<object?> tarea)
        {
            consultas++;
            return tarea;
        }
        var apertura = SeleccionarFila(cut, "Segunda conversación");
        cut.WaitForAssertion(() => consultas.Should().Be(1, "el detalle de la segunda ya está asignado y la carga espera a su Cliente empresarial"));

        cut.FindAll(".bandeja-cliente").Should().BeEmpty("el contexto del Cliente empresarial de la primera no puede verse bajo la segunda");

        await cut.InvokeAsync(() => clienteDeLaSegunda.SetResult(new ClienteDetalleDto(
            ClienteEbro.Id, ClienteEbro.RazonSocial, "A22222222", false, null, DateTime.UtcNow, null, Guid.NewGuid())));
        await apertura;

        cut.WaitForAssertion(() => cut.Find(".bandeja-cliente").TextContent.Should().Contain(ClienteEbro.RazonSocial).And.NotContain(ClienteRefrielectric.RazonSocial));
    }

    /// <summary>
    /// Segunda pasada de la revisión puente del delta r1 de la #2167 (MEDIO): dos cargas solapadas de la misma conversación. La
    /// primera (al abrirla desde otra) ya asignó el detalle pero espera el contexto de su Cliente empresarial; entra un mensaje
    /// nuevo y la recarga falla. El Id del detalle ya es el de la conversación, pero el contexto en memoria sigue siendo el de
    /// la anterior y no puede quedar bajo el hilo nuevo.
    /// </summary>
    [Fact]
    public async Task Una_recarga_fallida_con_la_carga_anterior_a_medias_no_deja_el_contexto_de_la_conversacion_previa()
    {
        var escenario = new Escenario();
        var primera = Conversacion("Primera conversación", ClienteRefrielectric);
        var segunda = Conversacion("Segunda conversación", ClienteEbro);
        escenario.Conversaciones.AddRange([primera, segunda]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");
        cut.FindAll(".bandeja-cliente").Should().NotBeEmpty("control positivo: con la primera abierta hay columna de contexto");

        var clienteDeLaSegunda = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lecturasDeLaSegunda = 0;
        escenario.Interceptar = p => p switch
        {
            ObtenerClientePorIdQuery q when q.Id == ClienteEbro.Id => clienteDeLaSegunda.Task,
            ObtenerConversacionPorIdQuery q when q.Id == segunda.Id && ++lecturasDeLaSegunda > 1 =>
                Task.FromException<object?>(new InvalidOperationException("boom")),
            _ => null
        };
        var apertura = SeleccionarFila(cut, "Segunda conversación");
        cut.WaitForAssertion(() => lecturasDeLaSegunda.Should().Be(1, "la primera carga ya asignó el detalle y espera al Cliente empresarial"));

        await cut.InvokeAsync(() => (Task)typeof(Bandeja).GetMethod("AlRecibirMensajeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cut.Instance, [new MensajeWhatsAppRecibidoEvent(Guid.NewGuid(), segunda.Id)])!);

        cut.WaitForAssertion(() => cut.Find(".bandeja-centro .estado-vacio h3").TextContent.Trim().Should().Be("No pudimos abrir la conversación"));
        cut.FindAll(".bandeja-cliente").Should().BeEmpty("el contexto del Cliente empresarial de la primera no puede quedar bajo la segunda");

        await cut.InvokeAsync(() => clienteDeLaSegunda.SetException(new InvalidOperationException("tarde")));
        await apertura;
    }
    /// <summary>
    /// Tercera pasada de la revisión puente del delta r1 de la #2167 (MEDIO): la conversación abierta pasa a otro Cliente
    /// empresarial (desde la triage) y la recarga espera el contexto del nuevo; entra un mensaje y la segunda recarga falla. El
    /// detalle ya es el del Cliente nuevo, pero el contexto en memoria sigue siendo el del anterior y no puede quedar bajo el hilo.
    /// </summary>
    [Fact]
    public async Task Una_recarga_fallida_tras_cambiar_el_Cliente_de_la_conversacion_no_deja_el_contexto_del_anterior()
    {
        var escenario = new Escenario();
        var primera = Conversacion("Primera conversación", ClienteRefrielectric);
        escenario.Conversaciones.Add(primera);
        escenario.Detalle = _ => DetalleDe(primera);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");
        cut.FindAll(".bandeja-cliente").Should().NotBeEmpty("control positivo: con la conversación abierta hay columna de contexto");

        escenario.Detalle = _ => DetalleDe(primera) with { ClienteId = ClienteEbro.Id, ClienteRazonSocial = ClienteEbro.RazonSocial };
        var clienteNuevo = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lecturas = 0;
        escenario.Interceptar = p => p switch
        {
            ObtenerClientePorIdQuery q when q.Id == ClienteEbro.Id => clienteNuevo.Task,
            ObtenerConversacionPorIdQuery q when q.Id == primera.Id && ++lecturas > 1 =>
                Task.FromException<object?>(new InvalidOperationException("boom")),
            _ => null
        };
        Task Recibir() => cut.InvokeAsync(() => (Task)typeof(Bandeja).GetMethod("AlRecibirMensajeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cut.Instance, [new MensajeWhatsAppRecibidoEvent(Guid.NewGuid(), primera.Id)])!);
        var primeraRecarga = Recibir();
        cut.WaitForAssertion(() => lecturas.Should().Be(1, "la primera recarga ya asignó el detalle y espera al Cliente empresarial nuevo"));

        await Recibir();

        cut.WaitForAssertion(() => cut.Find(".bandeja-centro .estado-vacio h3").TextContent.Trim().Should().Be("No pudimos abrir la conversación"));
        cut.FindAll(".bandeja-cliente").Should().BeEmpty("el contexto del Cliente empresarial anterior no puede quedar bajo el hilo del nuevo");

        await cut.InvokeAsync(() => clienteNuevo.SetException(new InvalidOperationException("tarde")));
        await primeraRecarga;
    }
    private static Task RecibirMensajeAsync(IRenderedComponent<Bandeja> cut, Guid conversacionId) =>
        cut.InvokeAsync(() => (Task)typeof(Bandeja).GetMethod("AlRecibirMensajeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cut.Instance, [new MensajeWhatsAppRecibidoEvent(Guid.NewGuid(), conversacionId)])!);

    private static ClienteDetalleDto FichaDeEbro() =>
        new(ClienteEbro.Id, ClienteEbro.RazonSocial, "A22222222", false, null, DateTime.UtcNow, null, Guid.NewGuid());

    /// <summary>
    /// Revisión Codex r3 de la #2167, MEDIO 1: con la nota fija en edición, la recarga del mismo hilo cambia su Cliente empresarial.
    /// Mientras la carga espera el contexto del nuevo, el contexto anterior no se pinta (ficha) pero el componente de la nota fija no
    /// se desmonta: el borrador sigue en su área y, al llegar el contexto, se pinta la ficha nueva. Detecta volver a desmontar el
    /// panel cuando <c>ContextoEsDelDetalle</c> es falso (PanelContextoVisible).
    /// </summary>
    [Fact]
    public async Task Si_la_recarga_cambia_el_Cliente_con_la_nota_fija_en_edicion_el_borrador_no_se_desmonta()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion), NotaContacto = NotaDeCarmen() };
        escenario.Conversaciones.Add(conversacion);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".nota-fija").Should().ContainSingle());
        await Boton(cut, ".nota-fija button", "Editar").ClickAsync(new MouseEventArgs());
        await cut.Find(".nota-fija-area").InputAsync(new ChangeEventArgs { Value = "Llamar por la tarde." });

        escenario.Detalle = _ => DetalleDe(conversacion) with { ClienteId = ClienteEbro.Id, ClienteRazonSocial = ClienteEbro.RazonSocial };
        var clienteNuevo = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consultas = 0;
        escenario.Interceptar = p => p is ObtenerClientePorIdQuery q && q.Id == ClienteEbro.Id ? Retener() : null;
        Task<object?> Retener()
        {
            consultas++;
            return clienteNuevo.Task;
        }
        var recarga = RecibirMensajeAsync(cut, conversacion.Id);
        cut.WaitForAssertion(() => consultas.Should().Be(1, "la recarga ya asignó el detalle y espera al Cliente empresarial nuevo"));
        cut.Render();

        cut.Find(".bandeja-cliente .nota-fija-area").GetAttribute("value").Should().Be("Llamar por la tarde.",
            "el borrador sigue en su área mientras el contexto se recarga");
        cut.FindAll(".bandeja-cliente-identidad").Should().BeEmpty("la ficha del Cliente empresarial anterior no puede verse bajo el detalle nuevo");

        await cut.InvokeAsync(() => clienteNuevo.SetResult(FichaDeEbro()));
        await recarga;

        cut.Find(".nota-fija-area").GetAttribute("value").Should().Be("Llamar por la tarde.");
        cut.FindComponent<NotaFijaContacto>().Instance.TieneTextoSinGuardar.Should().BeTrue();
        cut.Find(".bandeja-cliente-identidad").TextContent.Should().Contain(ClienteEbro.RazonSocial);
    }

    /// <summary>
    /// Revisión Codex r3 de la #2167, MEDIO 1 (segunda recarga): tras la recarga que cambia el Cliente empresarial y espera su
    /// contexto, una segunda recarga falla. Con la nota fija en edición, el detalle se conserva sin contexto y el borrador no se
    /// pierde; sin la nota en edición, el comportamiento sigue siendo vaciar el detalle (test de la tercera pasada del delta r1).
    /// Detecta quitar la excepción de la nota fija en el catch de <c>CargarDetalleAsync</c>.
    /// </summary>
    [Fact]
    public async Task Una_segunda_recarga_fallida_tras_cambiar_el_Cliente_no_destruye_el_borrador_de_la_nota_fija()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion), NotaContacto = NotaDeCarmen() };
        escenario.Conversaciones.Add(conversacion);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".nota-fija").Should().ContainSingle());
        await Boton(cut, ".nota-fija button", "Editar").ClickAsync(new MouseEventArgs());
        await cut.Find(".nota-fija-area").InputAsync(new ChangeEventArgs { Value = "Llamar por la tarde." });

        escenario.Detalle = _ => DetalleDe(conversacion) with { ClienteId = ClienteEbro.Id, ClienteRazonSocial = ClienteEbro.RazonSocial };
        var clienteNuevo = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lecturas = 0;
        escenario.Interceptar = p => p switch
        {
            ObtenerClientePorIdQuery q when q.Id == ClienteEbro.Id => clienteNuevo.Task,
            ObtenerConversacionPorIdQuery q when q.Id == conversacion.Id && ++lecturas > 1 =>
                Task.FromException<object?>(new InvalidOperationException("boom")),
            _ => null
        };
        var primeraRecarga = RecibirMensajeAsync(cut, conversacion.Id);
        cut.WaitForAssertion(() => lecturas.Should().Be(1, "la primera recarga ya asignó el detalle y espera al Cliente empresarial nuevo"));

        await RecibirMensajeAsync(cut, conversacion.Id);

        cut.WaitForAssertion(() => Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(
            m => m.Mensaje.StartsWith("No pudimos abrir la conversación"), "el fallo de la recarga se avisa"));
        cut.Render();
        cut.Find(".bandeja-cliente .nota-fija-area").GetAttribute("value").Should().Be("Llamar por la tarde.", "el borrador sobrevive a la segunda recarga fallida");
        cut.FindAll(".bandeja-cliente-identidad").Should().BeEmpty("el contexto del Cliente empresarial anterior no puede quedar bajo el hilo");
        cut.FindAll(".bandeja-centro .estado-vacio").Should().BeEmpty("mientras se edita, el hilo sigue en pantalla");

        // Revisión puente de la r3 (MEDIO): el detalle sin contexto solo dura mientras la nota se edita. Al cancelar la edición la
        // página vuelve al estado de error con «Reintentar», no se queda con un hilo sin ficha ni salida.
        await Boton(cut, ".nota-fija button", "Cancelar").ClickAsync(new MouseEventArgs());
        cut.Find(".bandeja-centro .estado-vacio h3").TextContent.Trim().Should().Be("No pudimos abrir la conversación");
        cut.Find(".bandeja-centro .estado-vacio button").TextContent.Trim().Should().NotBeEmpty("con su «Reintentar»");

        await cut.InvokeAsync(() => clienteNuevo.SetException(new InvalidOperationException("tarde")));
        await primeraRecarga;
    }

    /// <summary>
    /// Revisión Codex r3 de la #2167, MEDIO 2: la carga de una conversación sin Cliente empresarial leía las macros y las asignaba
    /// antes de comprobar si seguía siendo la vigente. Con la conversación A leyendo sus macros, se abre B (que fija las suyas) y
    /// después acaba la lectura de A: no puede sustituir las de B. Detecta volver a asignar <c>_macrosDisponibles</c> antes de la guarda.
    /// </summary>
    [Fact]
    public async Task Una_lectura_tardia_de_macros_de_otra_conversacion_sin_Cliente_no_sustituye_las_de_la_abierta()
    {
        var escenario = new Escenario();
        var primera = Conversacion("Primera sin Cliente");
        var segunda = Conversacion("Segunda sin Cliente");
        escenario.Conversaciones.AddRange([primera, segunda]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        escenario.Macros.Add(new MacroListaDto(Guid.NewGuid(), null, null, "Plantilla vigente", "<p>vigente</p>", Guid.NewGuid()));
        var macrosDeLaPrimera = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lecturas = 0;
        escenario.Interceptar = p => p is ObtenerMacrosQuery && ++lecturas == 1 ? macrosDeLaPrimera.Task : null;
        var (cut, _) = Renderizar(escenario);

        var aperturaDeLaPrimera = SeleccionarFila(cut, "Primera sin Cliente");
        cut.WaitForAssertion(() => lecturas.Should().Be(1, "la carga de la primera espera sus macros"));
        await SeleccionarFila(cut, "Segunda sin Cliente");
        cut.WaitForAssertion(() => cut.FindAll(".composer-macro-campo select option").Select(o => o.TextContent.Trim())
            .Should().Contain("Plantilla vigente", "control positivo: la segunda ya fijó sus macros"));

        await cut.InvokeAsync(() => macrosDeLaPrimera.SetResult(new List<MacroListaDto>
            { new(Guid.NewGuid(), null, null, "Plantilla de la carga antigua", "<p>antigua</p>", Guid.NewGuid()) }));
        await aperturaDeLaPrimera;
        cut.Render();

        TituloAbierto(cut).Should().Be("Segunda sin Cliente");
        cut.FindAll(".composer-macro-campo select option").Select(o => o.TextContent.Trim())
            .Should().Contain("Plantilla vigente").And.NotContain("Plantilla de la carga antigua");
    }

    /// <summary>
    /// Revisión Codex r3 de la #2167, MEDIO 4: «Compartir formatos de centro» espera al servidor y, si el Gestor CAE abre otra
    /// conversación mientras tanto, el resumen del Centro anterior acababa en la respuesta de la nueva. El resultado se descarta
    /// si la conversación que lo pidió ya no es la abierta. Detecta quitar la comprobación tras el await.
    /// </summary>
    [Fact]
    public async Task Los_formatos_de_un_centro_que_llegan_tarde_no_acaban_en_la_respuesta_de_otra_conversacion()
    {
        var escenario = new Escenario();
        var primera = Conversacion("Primera conversación", ClienteRefrielectric);
        var segunda = Conversacion("Segunda conversación", ClienteRefrielectric, "laura.gil@refrielectric.es");
        escenario.Conversaciones.AddRange([primera, segunda]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        var formatos = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peticiones = 0;
        escenario.Interceptar = p => p switch
        {
            ObtenerCentrosParaSelectorQuery => Ya(new List<CentroSelectorDto>
                { new(CentroNorteId, "Centro Norte", ClienteRefrielectric.RazonSocial, "Refrielectric S.A.") }),
            ObtenerFormatosRequeridosCentroQuery => Retener(),
            _ => null
        };
        Task<object?> Retener()
        {
            peticiones++;
            return formatos.Task;
        }
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");
        cut.WaitForAssertion(() => cut.FindAll(".composer-correo-prellenados select").Should().ContainSingle());

        var comparticion = cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
        cut.WaitForAssertion(() => peticiones.Should().Be(1, "el resumen del Centro está en vuelo"));
        await SeleccionarFila(cut, "Segunda conversación");
        cut.WaitForAssertion(() => TituloAbierto(cut).Should().Be("Segunda conversación"));

        await cut.InvokeAsync(() => formatos.SetResult("FORMATOS DEL CENTRO NORTE"));
        await comparticion;
        cut.Render();

        cut.FindComponent<ComposerBar>().Instance.Texto.Should().BeEmpty("la respuesta de la segunda conversación no recibe lo pedido desde la primera");
    }

    /// <summary>
    /// Revisión Codex r4 de la #2167, MEDIO 4: comparar solo el identificador de la conversación deja pasar A→B→A. La respuesta
    /// pedida en la primera apertura de A llega cuando A está abierta otra vez, con un borrador que ya no es el de entonces.
    /// Detecta volver a comparar solo <c>_conversacionSeleccionadaId</c> tras el await (o quitar la guarda de carga vigente).
    /// </summary>
    [Fact]
    public async Task Los_formatos_pedidos_en_una_apertura_anterior_de_la_misma_conversacion_no_acaban_en_la_respuesta()
    {
        var escenario = new Escenario();
        var primera = Conversacion("Primera conversación", ClienteRefrielectric);
        var segunda = Conversacion("Segunda conversación", ClienteRefrielectric, "laura.gil@refrielectric.es");
        escenario.Conversaciones.AddRange([primera, segunda]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        var formatos = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peticiones = 0;
        escenario.Interceptar = p => p switch
        {
            ObtenerCentrosParaSelectorQuery => Ya(new List<CentroSelectorDto>
                { new(CentroNorteId, "Centro Norte", ClienteRefrielectric.RazonSocial, "Refrielectric S.A.") }),
            ObtenerFormatosRequeridosCentroQuery => Retener(),
            _ => null
        };
        Task<object?> Retener()
        {
            peticiones++;
            return formatos.Task;
        }
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");
        cut.WaitForAssertion(() => cut.FindAll(".composer-correo-prellenados select").Should().ContainSingle());

        var comparticion = cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
        cut.WaitForAssertion(() => peticiones.Should().Be(1, "el resumen del Centro está en vuelo"));
        await SeleccionarFila(cut, "Segunda conversación");
        cut.WaitForAssertion(() => TituloAbierto(cut).Should().Be("Segunda conversación"));
        await SeleccionarFila(cut, "Primera conversación");
        cut.WaitForAssertion(() => cut.FindAll(".composer-correo-prellenados select").Should().ContainSingle(
            "control positivo: la primera conversación está abierta otra vez con su composer"));

        await cut.InvokeAsync(() => formatos.SetResult("FORMATOS DEL CENTRO NORTE"));
        await comparticion;
        cut.Render();

        TituloAbierto(cut).Should().Be("Primera conversación");
        peticiones.Should().Be(1, "la reapertura no repite la petición: lo que llega es la respuesta de la apertura anterior");
        cut.FindComponent<ComposerBar>().Instance.Texto.Should().BeEmpty("la respuesta pedida en una apertura anterior no se añade al borrador de la actual");
    }

    /// <summary>
    /// Revisión Codex r4 de la #2167, MEDIO 4: mientras se esperan los formatos, una recarga de la misma conversación cambia su
    /// Cliente empresarial. El resumen del Centro del Cliente empresarial anterior no puede acabar en una respuesta que ya es de
    /// otro contexto. Detecta comparar solo la conversación tras el await (la conversación abierta no ha cambiado).
    /// </summary>
    [Fact]
    public async Task Los_formatos_que_llegan_tras_cambiar_el_Cliente_empresarial_de_la_conversacion_no_acaban_en_la_respuesta()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var formatos = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peticiones = 0;
        escenario.Interceptar = p => p switch
        {
            ObtenerCentrosParaSelectorQuery => Ya(new List<CentroSelectorDto>
                { new(CentroNorteId, "Centro Norte", ClienteRefrielectric.RazonSocial, "Refrielectric S.A.") }),
            ObtenerClientePorIdQuery q when q.Id == ClienteEbro.Id => Ya(FichaDeEbro()),
            ObtenerFormatosRequeridosCentroQuery => Retener(),
            _ => null
        };
        Task<object?> Retener()
        {
            peticiones++;
            return formatos.Task;
        }
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".composer-correo-prellenados select").Should().ContainSingle());

        var comparticion = cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
        cut.WaitForAssertion(() => peticiones.Should().Be(1, "el resumen del Centro está en vuelo"));

        escenario.Detalle = _ => DetalleDe(conversacion) with { ClienteId = ClienteEbro.Id, ClienteRazonSocial = ClienteEbro.RazonSocial };
        await RecibirMensajeAsync(cut, conversacion.Id);
        cut.WaitForAssertion(() => cut.Find(".bandeja-cliente-identidad").TextContent.Should().Contain(ClienteEbro.RazonSocial,
            "control positivo: la recarga ya fijó el Cliente empresarial nuevo"));

        await cut.InvokeAsync(() => formatos.SetResult("FORMATOS DEL CENTRO NORTE"));
        await comparticion;
        cut.Render();

        TituloAbierto(cut).Should().Be("Documentación pendiente");
        cut.FindComponent<ComposerBar>().Instance.Texto.Should().BeEmpty("el Centro pedido era del Cliente empresarial anterior");

        // Hueco de la r4: el aviso y el selector liberado. El Centro sigue entre los disponibles tras la recarga (el doble devuelve
        // el mismo para cualquier Cliente), así que lo que vacía el selector es la guarda de los formatos, no la de los Centros.
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(
            m => m.Mensaje.StartsWith("La conversación se ha actualizado mientras se preparaba el resumen del Centro"),
            "el Gestor CAE sabe por qué no se añadió nada");
        cut.FindComponent<ComposerBar>().Instance.CentroFormatosSeleccionado.Should().BeEmpty("el selector queda libre para volver a elegirlo");
    }

    /// <summary>
    /// Revisión Codex r4 de la #2167, MEDIO 4 (Centro): el Gestor CAE elige un Centro y, mientras espera, otro. Solo la última
    /// petición es la vigente: la respuesta tardía del primer Centro no se añade. Detecta quitar la guarda de petición vigente.
    /// </summary>
    [Fact]
    public async Task Los_formatos_de_un_centro_sustituido_por_otro_durante_la_espera_no_acaban_en_la_respuesta()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var centroSurId = Guid.NewGuid();
        var formatosNorte = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p switch
        {
            ObtenerCentrosParaSelectorQuery => Ya(new List<CentroSelectorDto>
            {
                new(CentroNorteId, "Centro Norte", ClienteRefrielectric.RazonSocial, "Refrielectric S.A."),
                new(centroSurId, "Centro Sur", ClienteRefrielectric.RazonSocial, "Refrielectric S.A.")
            }),
            ObtenerFormatosRequeridosCentroQuery q when q.CentroId == CentroNorteId => formatosNorte.Task,
            ObtenerFormatosRequeridosCentroQuery q when q.CentroId == centroSurId => Ya("FORMATOS DEL CENTRO SUR"),
            _ => null
        };
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".composer-correo-prellenados select").Should().ContainSingle());

        var comparticionNorte = cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
        await cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = centroSurId.ToString() });
        cut.WaitForAssertion(() => cut.FindComponent<ComposerBar>().Instance.Texto.Should().Be("FORMATOS DEL CENTRO SUR",
            "control positivo: la petición vigente sí se añade"));

        await cut.InvokeAsync(() => formatosNorte.SetResult("FORMATOS DEL CENTRO NORTE"));
        await comparticionNorte;
        cut.Render();

        cut.FindComponent<ComposerBar>().Instance.Texto.Should().Be("FORMATOS DEL CENTRO SUR", "el Centro sustituido no se añade");
    }

    // ---------------------------------------------------------------- revisión Codex r5 de la #2167: inventario de caminos asíncronos

    private static readonly Guid CentroSurEbroId = Guid.NewGuid();

    /// <summary>Centros por Cliente empresarial: Refrielectric tiene el Centro Norte; Ebro, solo el Centro Sur.</summary>
    private static Task<object?>? CentrosPorCliente(object peticion) => peticion switch
    {
        ObtenerCentrosParaSelectorQuery q when q.ClienteId == ClienteEbro.Id => Ya(new List<CentroSelectorDto>
            { new(CentroSurEbroId, "Centro Sur", ClienteEbro.RazonSocial, ClienteEbro.RazonSocial) }),
        ObtenerCentrosParaSelectorQuery => Ya(new List<CentroSelectorDto>
            { new(CentroNorteId, "Centro Norte", ClienteRefrielectric.RazonSocial, "Refrielectric S.A.") }),
        ObtenerFormatosRequeridosCentroQuery => Ya("FORMATOS DEL CENTRO NORTE"),
        ObtenerBorradorPedirPrioridadQuery => Ya(BorradorDelCentroNorte()),
        PedirPrioridadValidacionCommand => Ya(Result.Exito()),
        _ => null
    };

    private static Result<BorradorPedirPrioridadDto> BorradorDelCentroNorte() => Result.Exito(new BorradorPedirPrioridadDto(
        "validacion@example.invalid", "Prioridad Centro Norte", "<p>Rogamos prioridad.</p>", true, null, 2));

    private static ConversacionDetalleDto DetalleConEbro(ConversacionListaDto conversacion) =>
        DetalleDe(conversacion) with { ClienteId = ClienteEbro.Id, ClienteRazonSocial = ClienteEbro.RazonSocial };

    private static bool HayBotonPedirPrioridad(IRenderedComponent<Bandeja> cut) =>
        cut.FindAll("button").Any(b => b.TextContent.Trim() == "Pedir prioridad de validación");

    private static bool HayToast(BunitContext contexto, string inicio) =>
        contexto.Services.GetRequiredService<ToastService>().Mensajes.Any(m => m.Mensaje.StartsWith(inicio));

    /// <summary>
    /// Revisión Codex r5 de la #2167, MEDIO 2: el Gestor CAE elige un Centro y, mientras espera, vuelve a «Selecciona un centro…».
    /// La respuesta del Centro retirado no se añade. Detecta incrementar la petición vigente solo después de leer el Centro.
    /// </summary>
    [Fact]
    public async Task Los_formatos_de_un_centro_retirado_del_selector_durante_la_espera_no_acaban_en_la_respuesta()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var formatosNorte = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p is ObtenerFormatosRequeridosCentroQuery ? formatosNorte.Task : CentrosPorCliente(p);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".composer-correo-prellenados select").Should().ContainSingle());

        var comparticion = cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
        await cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = string.Empty });
        cut.FindComponent<ComposerBar>().Instance.CentroFormatosSeleccionado.Should().BeEmpty("control positivo: la selección se retiró");

        await cut.InvokeAsync(() => formatosNorte.SetResult("FORMATOS DEL CENTRO NORTE"));
        await comparticion;
        cut.Render();

        cut.FindComponent<ComposerBar>().Instance.Texto.Should().BeEmpty("el Centro retirado del selector no se añade");
    }

    /// <summary>
    /// Revisión Codex r5 de la #2167, MEDIO 3: una recarga cambia el Cliente empresarial y sus Centros ya no incluyen el elegido.
    /// La selección se vacía y «Pedir prioridad» deja de ofrecerse. Detecta quitar el vaciado de <c>CargarDetalleAsync</c>.
    /// </summary>
    [Fact]
    public async Task Si_la_recarga_cambia_el_Cliente_y_el_centro_elegido_ya_no_esta_se_retira_la_seleccion()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion), Interceptar = CentrosPorCliente };
        escenario.Conversaciones.Add(conversacion);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".composer-correo-prellenados select").Should().ContainSingle());
        await cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
        HayBotonPedirPrioridad(cut).Should().BeTrue("control positivo: con el Centro elegido se ofrece pedir prioridad");

        escenario.Detalle = _ => DetalleConEbro(conversacion);
        await RecibirMensajeAsync(cut, conversacion.Id);
        cut.WaitForAssertion(() => cut.Find(".bandeja-cliente-identidad").TextContent.Should().Contain(ClienteEbro.RazonSocial));

        cut.FindComponent<ComposerBar>().Instance.CentroFormatosSeleccionado.Should().BeEmpty("el Centro Norte no es del Cliente empresarial nuevo");
        HayBotonPedirPrioridad(cut).Should().BeFalse("«Pedir prioridad» no puede operar sobre el Centro del Cliente empresarial anterior");
    }

    /// <summary>
    /// Revisión Codex r5 de la #2167, MEDIO 3 (Drawer abierto): con el borrador de prioridad ya en pantalla, una recarga cambia el
    /// Cliente empresarial y retira el Centro. «Enviar» no manda la petición y dice por qué. Detecta quitar la guarda de
    /// <c>EnviarPrioridadAsync</c> (sin ella, el envío se descarta en silencio).
    /// </summary>
    [Fact]
    public async Task Pedir_prioridad_no_se_envia_si_una_recarga_retira_el_centro_con_el_borrador_abierto()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion), Interceptar = CentrosPorCliente };
        escenario.Conversaciones.Add(conversacion);
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".composer-correo-prellenados select").Should().ContainSingle());
        await cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Pedir prioridad de validación").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "Prioridad Centro Norte"));

        escenario.Detalle = _ => DetalleConEbro(conversacion);
        await RecibirMensajeAsync(cut, conversacion.Id);
        cut.WaitForAssertion(() => cut.Find(".bandeja-cliente-identidad").TextContent.Should().Contain(ClienteEbro.RazonSocial));

        await Boton(cut, ".drawer-pie button", "Enviar").ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<PedirPrioridadValidacionCommand>().Should().BeEmpty("el Centro del borrador ya no es de esta conversación");
        cut.Find(".drawer-panel").TextContent.Should().Contain("El Centro elegido ya no es de esta conversación");
    }

    /// <summary>
    /// Revisión Codex r5 de la #2167, MEDIO 3 (borrador en vuelo): mientras se prepara el borrador de prioridad, una recarga cambia
    /// el Cliente empresarial y retira el Centro. El borrador que llega tarde no se ofrece. Detecta quitar la comprobación tras el
    /// await de <c>AbrirPedirPrioridadAsync</c>.
    /// </summary>
    [Fact]
    public async Task El_borrador_de_prioridad_que_llega_tras_retirarse_el_centro_no_se_ofrece()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var borrador = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p is ObtenerBorradorPedirPrioridadQuery ? borrador.Task : CentrosPorCliente(p);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".composer-correo-prellenados select").Should().ContainSingle());
        await cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
        var apertura = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Pedir prioridad de validación").ClickAsync(new MouseEventArgs());

        escenario.Detalle = _ => DetalleConEbro(conversacion);
        await RecibirMensajeAsync(cut, conversacion.Id);
        cut.WaitForAssertion(() => cut.Find(".bandeja-cliente-identidad").TextContent.Should().Contain(ClienteEbro.RazonSocial));

        await cut.InvokeAsync(() => borrador.SetResult(BorradorDelCentroNorte()));
        await apertura;
        cut.Render();

        cut.FindComponents<CampoTexto>().Should().NotContain(c => c.Instance.Valor == "Prioridad Centro Norte",
            "el borrador era del Centro del Cliente empresarial anterior");
        cut.Find(".drawer-panel").TextContent.Should().Contain("El Centro elegido ya no es de esta conversación");
    }

    /// <summary>
    /// Revisión Codex r5 de la #2167, inventario de caminos asíncronos («Actualizar documentación»): mientras se cargan las
    /// Empresas del Cliente empresarial de la conversación, una recarga lo cambia. Las Empresas del anterior no llegan al selector:
    /// el modal se cierra y se avisa. Detecta quitar la comprobación de <c>_cargaDetalleVigente</c> de
    /// <c>AbrirActualizarDocumentoAsync</c>.
    /// </summary>
    [Fact]
    public async Task Si_la_recarga_cambia_el_Cliente_durante_la_deteccion_el_selector_no_ofrece_Empresas_del_anterior()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        ConversacionDetalleDto ConAdjunto(ConversacionDetalleDto detalle) => detalle with
        {
            Mensajes = [detalle.Mensajes[0] with { Adjuntos = [new AdjuntoDetalleDto(AdjuntoId, "tc2.pdf", "application/pdf", 2048)] }],
        };
        var escenario = new Escenario { Detalle = _ => ConAdjunto(DetalleDe(conversacion)) };
        escenario.Conversaciones.Add(conversacion);
        var empresasDelAnterior = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = peticion => peticion switch
        {
            ObtenerEmpresasParaSelectorQuery consulta when consulta.ClienteId == ClienteRefrielectric.Id => empresasDelAnterior.Task,
            ObtenerEmpresasParaSelectorQuery => Task.FromResult<object?>(new List<EmpresaSelectorDto> { new(Guid.NewGuid(), "Montajes Ebro S.L.") }),
            DetectarActualizacionDocumentoDesdeAdjuntoQuery => Task.FromResult<object?>(Result.Exito(new DeteccionActualizacionDocumentoDto(
                AdjuntoId, null, null, CaeManager.Domain.Documentos.AmbitoAplicacion.Empresa, null, null, null, null, null, null, 80))),
            _ => null,
        };
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");

        var apertura = cut.Find(".timeline-adjunto-actualizar-documento").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => mediador.Enviados.OfType<ObtenerEmpresasParaSelectorQuery>().Should().Contain(
            c => c.ClienteId == ClienteRefrielectric.Id, "las Empresas del Cliente empresarial de la apertura están en vuelo"));
        escenario.Detalle = _ => ConAdjunto(DetalleConEbro(conversacion));
        await RecibirMensajeAsync(cut, conversacion.Id);
        cut.WaitForAssertion(() => cut.Find(".bandeja-cliente-identidad").TextContent.Should().Contain(ClienteEbro.RazonSocial,
            "control positivo: la recarga ya fijó el Cliente empresarial nuevo"));

        await cut.InvokeAsync(() => empresasDelAnterior.SetResult(
            new List<EmpresaSelectorDto> { new(Guid.NewGuid(), "Instalaciones del Cliente anterior S.L.") }));
        await apertura;
        cut.Render();

        cut.Markup.Should().NotContain("Instalaciones del Cliente anterior S.L.", "el selector no ofrece Empresas del Cliente empresarial anterior");
        cut.FindAll(".modal-actualizar-documento-formulario").Should().BeEmpty("los selectores del modal eran de la carga anterior");
        HayToast(this, "La conversación se ha actualizado y se ha cerrado «Actualizar documentación»").Should().BeTrue("se dice por qué se cerró");
    }

    private static (Escenario Escenario, ConversacionListaDto Primera, ConversacionListaDto Segunda) DosConversacionesConPlantilla(MacroListaDto macro)
    {
        var escenario = new Escenario();
        var primera = Conversacion("Primera conversación", ClienteRefrielectric);
        var segunda = Conversacion("Segunda conversación", ClienteRefrielectric, "laura.gil@refrielectric.es");
        escenario.Conversaciones.AddRange([primera, segunda]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        escenario.Macros.Add(macro);
        return (escenario, primera, segunda);
    }

    private static MacroListaDto PlantillaCertificado() =>
        new(Guid.NewGuid(), null, null, "Certificado", "<p>Falta el certificado.</p>", Guid.NewGuid());

    /// <summary>
    /// Revisión Codex r5 de la #2167, MEDIO 1 (A→B→A): la plantilla pedida en la primera apertura de A llega con A abierta otra
    /// vez. No se añade al borrador de la apertura actual. Detecta volver a comparar solo la conversación tras el await.
    /// </summary>
    [Fact]
    public async Task Una_plantilla_pedida_en_una_apertura_anterior_de_la_misma_conversacion_no_acaba_en_la_respuesta()
    {
        var macro = PlantillaCertificado();
        var (escenario, _, _) = DosConversacionesConPlantilla(macro);
        var plantilla = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p is AplicarMacroCommand ? plantilla.Task : null;
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");

        var aplicacion = cut.Find(".composer-macro-campo select").ChangeAsync(new ChangeEventArgs { Value = macro.Id.ToString() });
        cut.WaitForAssertion(() => mediador.Enviados.OfType<AplicarMacroCommand>().Should().ContainSingle("la plantilla está en vuelo"));
        await SeleccionarFila(cut, "Segunda conversación");
        cut.WaitForAssertion(() => TituloAbierto(cut).Should().Be("Segunda conversación"));
        await SeleccionarFila(cut, "Primera conversación");
        cut.WaitForAssertion(() => cut.FindAll(".composer-macro-campo select").Should().ContainSingle(
            "control positivo: la primera conversación está abierta otra vez con su composer"));

        await cut.InvokeAsync(() => plantilla.SetResult(Result.Exito("<p>Plantilla de la apertura anterior.</p>")));
        await aplicacion;
        cut.Render();

        TituloAbierto(cut).Should().Be("Primera conversación");
        cut.FindComponent<ComposerBar>().Instance.Texto.Should().BeEmpty("la plantilla pedida en una apertura anterior no se añade al borrador de la actual");
    }

    /// <summary>
    /// Revisión Codex r5 de la #2167, MEDIO 1 (Cliente empresarial): mientras se rellena la plantilla, una recarga de la misma
    /// conversación cambia su Cliente empresarial. La plantilla rellenada con el contexto anterior no se añade y se avisa.
    /// </summary>
    [Fact]
    public async Task Una_plantilla_que_llega_tras_cambiar_el_Cliente_empresarial_no_acaba_en_la_respuesta()
    {
        var macro = PlantillaCertificado();
        var (escenario, primera, _) = DosConversacionesConPlantilla(macro);
        var plantilla = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p is AplicarMacroCommand ? plantilla.Task : null;
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");

        var aplicacion = cut.Find(".composer-macro-campo select").ChangeAsync(new ChangeEventArgs { Value = macro.Id.ToString() });
        cut.WaitForAssertion(() => mediador.Enviados.OfType<AplicarMacroCommand>().Should().ContainSingle("la plantilla está en vuelo"));
        escenario.Detalle = id => id == primera.Id ? DetalleConEbro(primera) : DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        await RecibirMensajeAsync(cut, primera.Id);
        cut.WaitForAssertion(() => cut.Find(".bandeja-cliente-identidad").TextContent.Should().Contain(ClienteEbro.RazonSocial,
            "control positivo: la recarga ya fijó el Cliente empresarial nuevo"));

        await cut.InvokeAsync(() => plantilla.SetResult(Result.Exito("<p>Plantilla con los datos de Refrielectric.</p>")));
        await aplicacion;
        cut.Render();

        cut.FindComponent<ComposerBar>().Instance.Texto.Should().BeEmpty("la plantilla se rellenó con el Cliente empresarial anterior");
        HayToast(this, "La conversación se ha actualizado mientras se preparaba la plantilla").Should().BeTrue("se dice por qué no se añadió");
    }

    /// <summary>
    /// Inventario de la r5 de la #2167 (adjuntos): la lectura de un archivo espera al navegador. Si mientras tanto se abre otra
    /// conversación, el archivo no acaba en su borrador. Detecta volver a leer sobre la lista del borrador o quitar la guarda.
    /// </summary>
    [Fact]
    public async Task Un_archivo_que_termina_de_leerse_tras_cambiar_de_conversacion_no_acaba_en_el_borrador_de_la_otra()
    {
        var (escenario, _, _) = DosConversacionesConPlantilla(PlantillaCertificado());
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");

        var puerta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var archivo = new ArchivoRetenido("tc2.pdf", [1, 2, 3], puerta.Task);
        var composer = cut.FindComponent<ComposerBar>();
        var lectura = cut.InvokeAsync(() => composer.Instance.OnArchivosSeleccionados.InvokeAsync(new InputFileChangeEventArgs([archivo])));
        cut.WaitForAssertion(() => archivo.Abierto.Should().BeTrue("la lectura está en curso"));
        await SeleccionarFila(cut, "Segunda conversación");
        cut.WaitForAssertion(() => TituloAbierto(cut).Should().Be("Segunda conversación"));

        puerta.SetResult();
        await lectura;
        cut.Render();
        cut.FindComponent<ComposerBar>().Instance.AdjuntosPendientes.Should().BeEmpty("el archivo se eligió en la primera conversación");

        // Control positivo: sin cambiar de conversación, lo leído sí se adjunta.
        var otro = new ArchivoRetenido("dni.pdf", [4, 5], Task.CompletedTask);
        await cut.InvokeAsync(() => cut.FindComponent<ComposerBar>().Instance.OnArchivosSeleccionados.InvokeAsync(new InputFileChangeEventArgs([otro])));
        cut.FindComponent<ComposerBar>().Instance.AdjuntosPendientes.Select(a => a.NombreArchivo).Should().Equal("dni.pdf");
    }

    /// <summary>
    /// Inventario de la r5 de la #2167 (envío): la respuesta de A se está enviando y el Gestor CAE abre B (descartando) y empieza
    /// otra respuesta. Al terminar el envío de A, el borrador de B no se vacía. Detecta volver a vaciar sin comprobar el borrador.
    /// </summary>
    [Fact]
    public async Task Una_respuesta_que_termina_de_enviarse_tras_cambiar_de_conversacion_no_vacia_el_borrador_de_la_otra()
    {
        var (escenario, _, _) = DosConversacionesConPlantilla(PlantillaCertificado());
        var envio = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p is ResponderConversacionCommand ? envio.Task : null;
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");
        await Escribir(cut, "Respuesta de la primera.");

        var envioPrimera = Enviar(cut);
        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().ContainSingle("control positivo: el envío está en vuelo");
        await AbrirDescartandoAsync(cut, "Segunda conversación");
        await cut.InvokeAsync(() => cut.FindComponent<ComposerBar>().Instance.TextoChanged.InvokeAsync("Borrador de la segunda."));

        await cut.InvokeAsync(() => envio.SetResult(Result.Exito()));
        await envioPrimera;
        cut.Render();

        TituloAbierto(cut).Should().Be("Segunda conversación");
        cut.FindComponent<ComposerBar>().Instance.Texto.Should().Be("Borrador de la segunda.", "el envío de la primera no vacía la respuesta de la segunda");
    }

    /// <summary>Mismo caso que el anterior por el fallback WhatsApp→correo (<c>EnviarFallbackCorreoAsync</c>).</summary>
    [Fact]
    public async Task Un_fallback_por_correo_que_termina_tras_cambiar_de_conversacion_no_vacia_el_borrador_de_la_otra()
    {
        var (escenario, _, _) = DosConversacionesConPlantilla(PlantillaCertificado());
        var envio = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p is CaeManager.Application.Comunicaciones.Commands.MigrarConversacionACorreo.MigrarConversacionACorreoCommand ? envio.Task : null;
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");
        var composer = cut.FindComponent<ComposerBar>();
        await cut.InvokeAsync(() => composer.Instance.TextoChanged.InvokeAsync("Respuesta de la primera."));
        await cut.InvokeAsync(() => composer.Instance.EmailFallbackChanged.InvokeAsync("carmen.ruiz@refrielectric.es"));

        var envioPrimera = cut.InvokeAsync(() => cut.FindComponent<ComposerBar>().Instance.OnEnviarFallbackCorreo.InvokeAsync());
        cut.WaitForAssertion(() => mediador.Enviados.OfType<CaeManager.Application.Comunicaciones.Commands.MigrarConversacionACorreo.MigrarConversacionACorreoCommand>().Should().ContainSingle("control positivo: el envío está en vuelo"));
        await AbrirDescartandoAsync(cut, "Segunda conversación");
        await cut.InvokeAsync(() => cut.FindComponent<ComposerBar>().Instance.TextoChanged.InvokeAsync("Borrador de la segunda."));
        await cut.InvokeAsync(() => cut.FindComponent<ComposerBar>().Instance.EmailFallbackChanged.InvokeAsync("laura.gil@refrielectric.es"));

        await cut.InvokeAsync(() => envio.SetResult(Result.Exito()));
        await envioPrimera;
        cut.Render();

        var actual = cut.FindComponent<ComposerBar>().Instance;
        actual.Texto.Should().Be("Borrador de la segunda.", "el fallback de la primera no vacía la respuesta de la segunda");
        actual.EmailFallback.Should().Be("laura.gil@refrielectric.es");
    }

    // ---------------------------------------------------------------- revisión Codex r6 de la #2167: apertura y contexto uniformes

    private static T CampoPrivado<T>(IRenderedComponent<Bandeja> cut, string nombre) =>
        (T)typeof(Bandeja).GetField(nombre, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cut.Instance)!;

    private static TaskCompletionSource<object?> Retenida() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completa una respuesta retenida y espera a que la página la haya procesado hasta su siguiente espera, sin tiempos.
    /// <see cref="Retenida"/> no ejecuta las continuaciones en línea: al completarse, la continuación del mediador falso (que
    /// capturó el contexto del renderer) se encola en el despachador de bUnit, que procesa en orden de llegada. La invocación
    /// vacía que se encola después corre, por tanto, cuando la página ya pasó por esa continuación y quedó en su siguiente
    /// <c>await</c> (o terminó). Se encolan dos para cubrir un salto más de contexto sin depender de cuántos haya exactamente.
    /// </summary>
    private static async Task CompletarYProcesarAsync(IRenderedComponent<Bandeja> cut, TaskCompletionSource<object?> retenida, object? valor)
    {
        await cut.InvokeAsync(() => retenida.SetResult(valor));
        await cut.InvokeAsync(() => { });
        await cut.InvokeAsync(() => { });
    }

    private static ArchivoRetenido ArchivoListo(string nombre) => new(nombre, [1, 2, 3], Task.CompletedTask);

    private static Task AdjuntarAsync(IRenderedComponent<Bandeja> cut, ArchivoRetenido archivo) =>
        cut.InvokeAsync(() => cut.FindComponent<ComposerBar>().Instance.OnArchivosSeleccionados.InvokeAsync(new InputFileChangeEventArgs([archivo])));

    private static ConversacionDetalleDto ConAdjuntoTc2(ConversacionDetalleDto detalle) => detalle with
    {
        Mensajes = [detalle.Mensajes[0] with { Adjuntos = [new AdjuntoDetalleDto(AdjuntoId, "tc2.pdf", "application/pdf", 2048)] }],
    };

    /// <summary>
    /// Revisión Codex r6 de la #2167, MEDIO 1: el detalle de una conversación está en vuelo y el Gestor CAE cambia a «Mi buzón
    /// personal». El detalle que llega tarde no queda como detalle vigente sin selección. Detecta quitar el
    /// <c>++_cargaDetalleVigente</c> de <c>CambiarVistaAsync</c>.
    /// </summary>
    [Fact]
    public async Task Cambiar_de_vista_con_el_detalle_en_vuelo_no_deja_la_conversacion_abandonada_como_detalle()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var detalle = Retenida();
        escenario.Interceptar = p => p is ObtenerConversacionPorIdQuery ? detalle.Task : null;
        var (cut, mediador) = Renderizar(escenario);

        var apertura = SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => mediador.Enviados.OfType<ObtenerConversacionPorIdQuery>().Should().ContainSingle("el detalle está en vuelo"));
        await cut.FindAll("nav.bandeja-nav .bandeja-nav-item")[1].ClickAsync(new MouseEventArgs());
        mediador.Enviados.OfType<ObtenerMensajesBuzonPersonalQuery>().Should().ContainSingle("control positivo: la vista cambió");

        await cut.InvokeAsync(() => detalle.SetResult(DetalleDe(conversacion)));
        await apertura;
        cut.Render();

        CampoPrivado<ConversacionDetalleDto?>(cut, "_detalle").Should().BeNull("el detalle era de la conversación que se abandonó al cambiar de vista");
    }

    /// <summary>
    /// Revisión Codex r6 de la #2167, MEDIO 1 (plantilla): la plantilla está en vuelo y el Gestor CAE cambia de vista. No se añade a
    /// ningún borrador. Detecta quitar <c>CambiarApertura()</c> de <c>CambiarVistaAsync</c>.
    /// </summary>
    [Fact]
    public async Task Una_plantilla_en_vuelo_al_cambiar_de_vista_no_acaba_en_ningun_borrador()
    {
        var macro = PlantillaCertificado();
        var (escenario, _, _) = DosConversacionesConPlantilla(macro);
        var plantilla = Retenida();
        escenario.Interceptar = p => p is AplicarMacroCommand ? plantilla.Task : null;
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");

        var aplicacion = cut.Find(".composer-macro-campo select").ChangeAsync(new ChangeEventArgs { Value = macro.Id.ToString() });
        cut.WaitForAssertion(() => mediador.Enviados.OfType<AplicarMacroCommand>().Should().ContainSingle("la plantilla está en vuelo"));
        await cut.FindAll("nav.bandeja-nav .bandeja-nav-item")[1].ClickAsync(new MouseEventArgs());
        mediador.Enviados.OfType<ObtenerMensajesBuzonPersonalQuery>().Should().ContainSingle("control positivo: la vista cambió");

        await cut.InvokeAsync(() => plantilla.SetResult(Result.Exito("<p>Plantilla de la vista anterior.</p>")));
        await aplicacion;
        cut.Render();

        CampoPrivado<string>(cut, "_textoRespuesta").Should().BeEmpty("la plantilla se pidió para la conversación abandonada");
    }

    /// <summary>
    /// Revisión Codex r6 de la #2167, MEDIO 2: durante el envío el Gestor CAE añade otro adjunto. Al terminar se quita solo lo
    /// enviado. Detecta volver a vaciar todos los adjuntos en <c>LimpiarLoEnviado</c>.
    /// </summary>
    [Fact]
    public async Task Un_adjunto_anadido_durante_el_envio_sigue_en_el_borrador()
    {
        var (escenario, _, _) = DosConversacionesConPlantilla(PlantillaCertificado());
        var envio = Retenida();
        escenario.Interceptar = p => p is ResponderConversacionCommand ? envio.Task : null;
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");
        await Escribir(cut, "Respuesta con el TC2.");
        await AdjuntarAsync(cut, ArchivoListo("tc2.pdf"));

        var envioPrimera = Enviar(cut);
        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().ContainSingle("control positivo: el envío está en vuelo");
        // Mismo nombre a propósito: lo enviado se identifica por instancia, no por nombre.
        await AdjuntarAsync(cut, ArchivoListo("tc2.pdf"));
        cut.FindComponent<ComposerBar>().Instance.AdjuntosPendientes.Should().HaveCount(2, "control positivo: el segundo adjunto entró durante el envío");

        await cut.InvokeAsync(() => envio.SetResult(Result.Exito()));
        await envioPrimera;
        cut.Render();

        var composer = cut.FindComponent<ComposerBar>().Instance;
        composer.Texto.Should().BeEmpty("el texto enviado se retira");
        composer.AdjuntosPendientes.Select(a => a.NombreArchivo).Should().Equal(["tc2.pdf"], "solo se retira el adjunto enviado, no el que se llama igual");
    }

    /// <summary>
    /// Revisión Codex r6 de la #2167, MEDIO 2 (A→B→A con el mismo texto): el borrador de la reapertura no es el enviado aunque el
    /// texto coincida. Detecta comparar solo conversación y texto en <c>LimpiarLoEnviado</c>.
    /// </summary>
    [Fact]
    public async Task Un_envio_que_termina_tras_reabrir_la_conversacion_no_vacia_el_borrador_nuevo_con_el_mismo_texto()
    {
        var (escenario, _, _) = DosConversacionesConPlantilla(PlantillaCertificado());
        var envio = Retenida();
        escenario.Interceptar = p => p is ResponderConversacionCommand ? envio.Task : null;
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");
        await Escribir(cut, "Te lo envío hoy.");

        var envioPrimera = Enviar(cut);
        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().ContainSingle("control positivo: el envío está en vuelo");
        await AbrirDescartandoAsync(cut, "Segunda conversación");
        await SeleccionarFila(cut, "Primera conversación");
        cut.WaitForAssertion(() => TituloAbierto(cut).Should().Be("Primera conversación"));
        await cut.InvokeAsync(() => cut.FindComponent<ComposerBar>().Instance.TextoChanged.InvokeAsync("Te lo envío hoy."));

        await cut.InvokeAsync(() => envio.SetResult(Result.Exito()));
        await envioPrimera;
        cut.Render();

        cut.FindComponent<ComposerBar>().Instance.Texto.Should().Be("Te lo envío hoy.", "el borrador es de otra apertura");
    }

    /// <summary>
    /// Revisión Codex r6 de la #2167, MEDIO 2 (fallback): durante el envío por correo el Gestor CAE corrige la dirección. Al
    /// terminar se vacía el texto enviado y se conserva la dirección nueva. Detecta vaciar la dirección sin compararla.
    /// </summary>
    [Fact]
    public async Task Una_direccion_corregida_durante_el_fallback_por_correo_se_conserva()
    {
        var (escenario, _, _) = DosConversacionesConPlantilla(PlantillaCertificado());
        var envio = Retenida();
        escenario.Interceptar = p => p is CaeManager.Application.Comunicaciones.Commands.MigrarConversacionACorreo.MigrarConversacionACorreoCommand ? envio.Task : null;
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");
        var composer = cut.FindComponent<ComposerBar>();
        await cut.InvokeAsync(() => composer.Instance.TextoChanged.InvokeAsync("Respuesta de la primera."));
        await cut.InvokeAsync(() => composer.Instance.EmailFallbackChanged.InvokeAsync("carmen.ruiz@refrielectric.es"));

        var envioPrimera = cut.InvokeAsync(() => cut.FindComponent<ComposerBar>().Instance.OnEnviarFallbackCorreo.InvokeAsync());
        cut.WaitForAssertion(() => mediador.Enviados.OfType<CaeManager.Application.Comunicaciones.Commands.MigrarConversacionACorreo.MigrarConversacionACorreoCommand>().Should().ContainSingle("control positivo: el envío está en vuelo"));
        await cut.InvokeAsync(() => cut.FindComponent<ComposerBar>().Instance.EmailFallbackChanged.InvokeAsync("carmen.ruiz@example.invalid"));

        await cut.InvokeAsync(() => envio.SetResult(Result.Exito()));
        await envioPrimera;
        cut.Render();

        var actual = cut.FindComponent<ComposerBar>().Instance;
        actual.Texto.Should().BeEmpty("el texto enviado se retira");
        actual.EmailFallback.Should().Be("carmen.ruiz@example.invalid", "la dirección corregida no es la enviada");
    }

    /// <summary>
    /// Revisión Codex r6 de la #2167, MEDIO 3: el Gestor CAE cancela el Drawer de prioridad mientras se prepara el borrador y lo
    /// reabre para el mismo Centro. El borrador de la primera apertura no se ofrece ni apaga el indicador de carga de la segunda.
    /// Con la respuesta antigua primero, detecta quitar la condición del <c>finally</c> (apagaría el indicador de la reapertura);
    /// con la nueva primero, detecta quitar el <c>return</c> tras el await de <c>AbrirPedirPrioridadAsync</c> (la antigua
    /// sobrescribiría el borrador ya mostrado). Con la cancelada fallando (revisión Codex r7 de la #2167, BAJO), detecta quitar la
    /// guarda del <c>catch</c>: el error de la apertura cancelada saldría en el Drawer reabierto.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task El_borrador_de_prioridad_de_una_apertura_cancelada_no_se_ofrece_en_la_reapertura(bool reaperturaResponde_primero, bool canceladaFalla)
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var primero = Retenida();
        var segundo = Retenida();
        var pedidos = 0;
        escenario.Interceptar = p => p is ObtenerBorradorPedirPrioridadQuery
            ? (Interlocked.Increment(ref pedidos) == 1 ? primero.Task : segundo.Task)
            : CentrosPorCliente(p);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".composer-correo-prellenados select").Should().ContainSingle());
        await cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });

        var primeraApertura = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Pedir prioridad de validación").ClickAsync(new MouseEventArgs());
        await PulsarCancelarEnAsync(cut, ".drawer-pie");
        var segundaApertura = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Pedir prioridad de validación").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Volatile.Read(ref pedidos).Should().Be(2, "control positivo: dos aperturas para el mismo Centro"));

        Task ResponderLaCancelada() => cut.InvokeAsync(() =>
        {
            if (canceladaFalla) primero.SetException(new InvalidOperationException("Fallo de la apertura cancelada."));
            else primero.SetResult(Result.Exito(new BorradorPedirPrioridadDto(
                "validacion@example.invalid", "Prioridad de la apertura cancelada", "<p>Antiguo.</p>", true, null, 2)));
        });
        Task ResponderLaReapertura() => cut.InvokeAsync(() => segundo.SetResult(BorradorDelCentroNorte()));

        if (reaperturaResponde_primero)
        {
            await ResponderLaReapertura();
            await segundaApertura;
            cut.WaitForAssertion(() => cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "Prioridad Centro Norte",
                "control positivo: la reapertura ya muestra su borrador"));
            await ResponderLaCancelada();
            await primeraApertura;
            cut.Render();
        }
        else
        {
            await ResponderLaCancelada();
            await primeraApertura;
            cut.Render();
            CampoPrivado<bool>(cut, "_cargandoBorradorPrioridad").Should().BeTrue("la reapertura sigue esperando su borrador");
            await ResponderLaReapertura();
            await segundaApertura;
            cut.Render();
        }

        var campos = cut.FindComponents<CampoTexto>();
        campos.Should().NotContain(c => c.Instance.Valor == "Prioridad de la apertura cancelada", "el borrador era de la apertura cancelada");
        campos.Should().Contain(c => c.Instance.Valor == "Prioridad Centro Norte", "queda el borrador de la reapertura");
        CampoPrivado<string?>(cut, "_mensajeErrorPrioridad").Should().BeNull("el desenlace de la apertura cancelada no toca el Drawer reabierto");
    }

    /// <summary>
    /// Revisión Codex r6 de la #2167, MEDIO 4: con «Actualizar documentación» ya cargado, una recarga cambia el Cliente empresarial.
    /// El modal se cierra y se avisa; una recarga sin cambio de Cliente empresarial no lo cierra. Detecta quitar el cierre del
    /// modal de <c>CambiarContexto</c>.
    /// </summary>
    [Fact]
    public async Task Una_recarga_que_cambia_el_Cliente_empresarial_cierra_Actualizar_documentacion_ya_cargado()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => ConAdjuntoTc2(DetalleDe(conversacion)) };
        escenario.Conversaciones.Add(conversacion);
        escenario.Interceptar = peticion => peticion switch
        {
            ObtenerEmpresasParaSelectorQuery => Ya(new List<EmpresaSelectorDto> { new(Guid.NewGuid(), "Instalaciones Norte S.L.") }),
            DetectarActualizacionDocumentoDesdeAdjuntoQuery => Ya(Result.Exito(new DeteccionActualizacionDocumentoDto(
                AdjuntoId, null, null, CaeManager.Domain.Documentos.AmbitoAplicacion.Empresa, null, null, null, null, null, null, 80))),
            _ => null,
        };
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        await cut.Find(".timeline-adjunto-actualizar-documento").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".modal-actualizar-documento-formulario").Should().ContainSingle("el modal está cargado"));

        await RecibirMensajeAsync(cut, conversacion.Id);
        cut.Render();
        cut.FindAll(".modal-actualizar-documento-formulario").Should().ContainSingle("control: una recarga con el mismo Cliente empresarial no lo cierra");

        escenario.Detalle = _ => ConAdjuntoTc2(DetalleConEbro(conversacion));
        await RecibirMensajeAsync(cut, conversacion.Id);
        cut.WaitForAssertion(() => cut.Find(".bandeja-cliente-identidad").TextContent.Should().Contain(ClienteEbro.RazonSocial));

        cut.FindAll(".modal-actualizar-documento-formulario").Should().BeEmpty("las Empresas del modal eran del Cliente empresarial anterior");
        HayToast(this, "La conversación se ha actualizado y se ha cerrado «Actualizar documentación»").Should().BeTrue("se dice por qué se cerró");
    }

    // ---------------------------------------------------------------- revisión Codex r7 de la #2167

    /// <summary>
    /// Revisión Codex r7 de la #2167, MEDIO 1: el resumen de formatos de un Centro llega MIENTRAS una recarga del detalle está en
    /// curso. Hasta que esa recarga termina no se sabe con qué Cliente empresarial queda la conversación, así que no se añade al
    /// borrador; al terminar, se añade solo si el Cliente empresarial no cambió (control) y se descarta con aviso si cambió.
    /// Detecta volver a comprobar el contexto sin esperar a la recarga (<c>ContextoConfirmadoAsync</c>).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Los_formatos_que_llegan_durante_una_recarga_esperan_a_saber_su_Cliente_empresarial(bool cambiaCliente)
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var formatos = Retenida();
        var recarga = Retenida();
        var retenerDetalle = false;
        escenario.Interceptar = p => p switch
        {
            ObtenerCentrosParaSelectorQuery => Ya(new List<CentroSelectorDto>
                { new(CentroNorteId, "Centro Norte", ClienteRefrielectric.RazonSocial, "Refrielectric S.A.") }),
            ObtenerClientePorIdQuery q when q.Id == ClienteEbro.Id => Ya(FichaDeEbro()),
            ObtenerFormatosRequeridosCentroQuery => formatos.Task,
            ObtenerConversacionPorIdQuery when Volatile.Read(ref retenerDetalle) => recarga.Task,
            _ => null
        };
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".composer-correo-prellenados select").Should().ContainSingle());

        var comparticion = cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
        cut.WaitForAssertion(() => mediador.Enviados.OfType<ObtenerFormatosRequeridosCentroQuery>().Should().ContainSingle("el resumen está en vuelo"));

        Volatile.Write(ref retenerDetalle, true);
        var detallesAntes = DetallesPedidos(mediador, conversacion.Id);
        var aviso = RecibirMensajeAsync(cut, conversacion.Id);
        cut.WaitForAssertion(() => DetallesPedidos(mediador, conversacion.Id).Should().Be(detallesAntes + 1, "control positivo: la recarga está en vuelo"));

        // La continuación del resumen corre fuera de esta llamada: se espera a que llegue a la guarda sin dar tiempos.
        await CompletarYProcesarAsync(cut, formatos, "FORMATOS DEL CENTRO NORTE");
        cut.Render();
        CampoPrivado<string>(cut, "_textoRespuesta").Should().BeEmpty(
            "con la recarga en curso aún no se sabe de qué Cliente empresarial es la conversación");

        await cut.InvokeAsync(() => recarga.SetResult(cambiaCliente ? DetalleConEbro(conversacion) : DetalleDe(conversacion)));
        await aviso;
        await comparticion;
        cut.Render();

        cut.FindComponent<ComposerBar>().Instance.Texto.Should().Be(cambiaCliente ? string.Empty : "FORMATOS DEL CENTRO NORTE",
            "con el mismo Cliente empresarial se añade al terminar la recarga (control); con otro, no");
        HayToast(this, "La conversación se ha actualizado mientras se preparaba el resumen del Centro").Should().Be(cambiaCliente);
    }

    /// <summary>
    /// Control del instrumento de la teoría anterior: sin recarga en curso, <see cref="CompletarYProcesarAsync"/> basta para que
    /// el resumen pase la guarda y llegue al borrador sin esperar a que termine el manejador. Si este control falla, la
    /// comprobación «aún vacío» de la teoría anterior podría pasar solo porque la continuación no había llegado a la guarda.
    /// </summary>
    [Fact]
    public async Task Control_completar_y_procesar_lleva_el_resumen_al_borrador_sin_recarga_en_curso()
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var formatos = Retenida();
        escenario.Interceptar = p => p switch
        {
            ObtenerCentrosParaSelectorQuery => Ya(new List<CentroSelectorDto>
                { new(CentroNorteId, "Centro Norte", ClienteRefrielectric.RazonSocial, "Refrielectric S.A.") }),
            ObtenerFormatosRequeridosCentroQuery => formatos.Task,
            _ => null
        };
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        cut.WaitForAssertion(() => cut.FindAll(".composer-correo-prellenados select").Should().ContainSingle());

        var comparticion = cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
        cut.WaitForAssertion(() => mediador.Enviados.OfType<ObtenerFormatosRequeridosCentroQuery>().Should().ContainSingle("el resumen está en vuelo"));

        await CompletarYProcesarAsync(cut, formatos, "FORMATOS DEL CENTRO NORTE");
        CampoPrivado<string>(cut, "_textoRespuesta").Should().Be("FORMATOS DEL CENTRO NORTE",
            "sin recarga en curso, la continuación ya pasó la guarda cuando vuelve CompletarYProcesarAsync");

        await comparticion;
    }

    /// <summary>
    /// Revisión Codex r7 de la #2167, MEDIO 1 (plantilla): mismo caso que el anterior con «Aplicar plantilla». Detecta quitar la
    /// espera a la recarga en <c>AplicarMacro</c>.
    /// </summary>
    [Fact]
    public async Task Una_plantilla_que_llega_durante_una_recarga_que_cambia_el_Cliente_empresarial_no_acaba_en_la_respuesta()
    {
        var macro = PlantillaCertificado();
        var (escenario, primera, _) = DosConversacionesConPlantilla(macro);
        var plantilla = Retenida();
        var recarga = Retenida();
        var retenerDetalle = false;
        escenario.Interceptar = p => p switch
        {
            AplicarMacroCommand => plantilla.Task,
            ObtenerConversacionPorIdQuery when Volatile.Read(ref retenerDetalle) => recarga.Task,
            _ => null
        };
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");

        var aplicacion = cut.Find(".composer-macro-campo select").ChangeAsync(new ChangeEventArgs { Value = macro.Id.ToString() });
        cut.WaitForAssertion(() => mediador.Enviados.OfType<AplicarMacroCommand>().Should().ContainSingle("la plantilla está en vuelo"));

        Volatile.Write(ref retenerDetalle, true);
        var detallesAntes = DetallesPedidos(mediador, primera.Id);
        var aviso = RecibirMensajeAsync(cut, primera.Id);
        cut.WaitForAssertion(() => DetallesPedidos(mediador, primera.Id).Should().Be(detallesAntes + 1, "control positivo: la recarga está en vuelo"));

        await CompletarYProcesarAsync(cut, plantilla, Result.Exito("<p>Plantilla con los datos de Refrielectric.</p>"));
        Volatile.Write(ref retenerDetalle, false);
        await cut.InvokeAsync(() => recarga.SetResult(DetalleConEbro(primera)));
        await aviso;
        await aplicacion;
        cut.Render();

        cut.FindComponent<ComposerBar>().Instance.Texto.Should().BeEmpty("la recarga terminó con otro Cliente empresarial");
        HayToast(this, "La conversación se ha actualizado mientras se preparaba la plantilla").Should().BeTrue("se dice por qué no se añadió");
    }

    /// <summary>
    /// Revisión puente de la r7 de la #2167: una plantilla que espera a una recarga del detalle no queda colgada si el Gestor CAE
    /// vuelve a la bandeja antes de que la recarga termine. Volver abandona la carga (no llegará a su <c>finally</c> como vigente)
    /// y libera a quien la esperaba. Detecta quitar <c>TerminarRecargaDetalle()</c> de <c>DescartarConversacionAbierta</c>: la
    /// aplicación de la plantilla seguiría pendiente y el selector bloqueado (<c>_aplicandoMacro</c>) hasta la próxima apertura.
    /// </summary>
    [Fact]
    public async Task Volver_a_la_bandeja_con_una_plantilla_esperando_a_la_recarga_la_libera_sin_aplicarla()
    {
        var macro = PlantillaCertificado();
        var (escenario, primera, _) = DosConversacionesConPlantilla(macro);
        var plantilla = Retenida();
        var recarga = Retenida();
        var retenerDetalle = false;
        escenario.Interceptar = p => p switch
        {
            AplicarMacroCommand => plantilla.Task,
            ObtenerConversacionPorIdQuery when Volatile.Read(ref retenerDetalle) => recarga.Task,
            _ => null
        };
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");

        var aplicacion = cut.Find(".composer-macro-campo select").ChangeAsync(new ChangeEventArgs { Value = macro.Id.ToString() });
        cut.WaitForAssertion(() => mediador.Enviados.OfType<AplicarMacroCommand>().Should().ContainSingle("la plantilla está en vuelo"));

        Volatile.Write(ref retenerDetalle, true);
        var detallesAntes = DetallesPedidos(mediador, primera.Id);
        var aviso = RecibirMensajeAsync(cut, primera.Id);
        cut.WaitForAssertion(() => DetallesPedidos(mediador, primera.Id).Should().Be(detallesAntes + 1, "control positivo: la recarga está en vuelo"));

        await CompletarYProcesarAsync(cut, plantilla, Result.Exito("<p>Plantilla con los datos de Refrielectric.</p>"));
        aplicacion.IsCompleted.Should().BeFalse("control: la plantilla espera a la recarga");

        await InvocarPrivado(cut, "VolverABandejaAsync");
        // Un tope, no una espera: sin la liberación, la aplicación no termina hasta que acabe la recarga, que sigue retenida.
        await aplicacion.WaitAsync(TimeSpan.FromSeconds(10));
        CampoPrivado<bool>(cut, "_aplicandoMacro").Should().BeFalse("el selector de plantillas queda libre");
        CampoPrivado<string>(cut, "_textoRespuesta").Should().BeEmpty("la conversación ya no está abierta");

        Volatile.Write(ref retenerDetalle, false);
        await cut.InvokeAsync(() => recarga.SetResult(DetalleDe(primera)));
        await aviso;
    }

    /// <summary>
    /// Revisión Codex r7 de la #2167, MEDIO 2: tras enviar la respuesta, el medidor cierra el tramo y el Gestor CAE abre otra
    /// conversación y empieza una nota interna. Al terminar el medidor no se recarga la abierta (desmontaría su nota interna).
    /// Detecta quitar la comprobación de la apertura tras <c>RegistrarAccionMedidaAsync</c> en <c>EnviarAsync</c>.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Enviar_con_el_medidor_en_vuelo_y_otra_conversacion_abierta_no_recarga_su_detalle(bool cambiaApertura)
    {
        var (escenario, a, _, c) = TresConversaciones();
        Services.AddSingleton<IAlertaOperativa>(new AlertaQueRelanza());
        var tramo = Retenida();
        escenario.Interceptar = p => p switch
        {
            ObtenerTiempoGestionConversacionQuery => Ya(new TiempoGestionConversacionDto(true, 0, 3600)),
            RegistrarTramoGestionCommand => tramo.Task,
            _ => null
        };
        var (cut, mediador) = Renderizar(escenario, "comunicaciones?estado=Abierta", medidorActivo: true);
        await SeleccionarFila(cut, a.Asunto);
        // El tramo del medidor cuenta segundos enteros desde que se abrió la conversación: sin ellos no registra nada.
        await Task.Delay(1200);
        await Escribir(cut, "Respuesta de A.");

        var envio = Enviar(cut);
        cut.WaitForAssertion(() => mediador.Enviados.OfType<RegistrarTramoGestionCommand>().Should().NotBeEmpty("el medidor está cerrando el tramo"));
        var abierta = a;
        if (cambiaApertura)
        {
            await AbrirPorIdAsync(cut, c);
            abierta = c;
            await cut.Find(".composer-nota-interna textarea").InputAsync(new ChangeEventArgs { Value = "Nota de C a medias." });
        }
        var detallesAntes = DetallesPedidos(mediador, abierta.Id);

        await cut.InvokeAsync(() => tramo.SetResult(Result.Exito()));
        await envio;
        cut.Render();

        DetallesPedidos(mediador, abierta.Id).Should().Be(detallesAntes + (cambiaApertura ? 0 : 1),
            "con la misma apertura se recarga el detalle (control); con otra conversación abierta, no");
        if (cambiaApertura)
            cut.FindComponent<ComposerNotaInterna>().Instance.TieneTextoSinGuardar.Should().BeTrue("la nota interna de C sigue escrita");
    }

    /// <summary>
    /// Revisión Codex r7 de la #2167, MEDIO 3: el recordatorio propuesto de A se está enviando y el Gestor CAE abre C y empieza
    /// una nota interna. Al terminar, el aviso del componente hijo no recarga C. Detecta volver a pasarle a
    /// <c>RecordatorioPropuesto</c> una recarga que no lleve la apertura con la que se pintó.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Un_recordatorio_enviado_con_otra_conversacion_abierta_no_recarga_su_detalle(bool cambiaApertura)
    {
        var (escenario, a, _, c) = TresConversaciones();
        var aprobacion = Retenida();
        escenario.Interceptar = p => p switch
        {
            ObtenerRecordatorioPropuestoConversacionQuery q when q.ConversacionId == a.Id => Ya(new RecordatorioPropuestoDto(
                Guid.NewGuid(), new DateTime(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 20, 5, 0, 0, DateTimeKind.Utc),
                "cae@mnorte.example")),
            CaeManager.Application.Comunicaciones.Commands.AprobarRecordatorioConversacion.AprobarRecordatorioConversacionCommand => aprobacion.Task,
            _ => null
        };
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, a.Asunto);
        cut.WaitForAssertion(() => cut.FindAll(".recordatorio-propuesto").Should().ContainSingle("A tiene un recordatorio propuesto"));
        var recordatorio = cut.FindComponent<RecordatorioPropuesto>();
        await recordatorio.FindAll("button").Single(b => b.TextContent.Trim() == "Revisar").ClickAsync(new MouseEventArgs());
        var envio = recordatorio.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Aprobar y enviar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => mediador.Enviados
            .OfType<CaeManager.Application.Comunicaciones.Commands.AprobarRecordatorioConversacion.AprobarRecordatorioConversacionCommand>()
            .Should().ContainSingle("el recordatorio está en vuelo"));
        var abierta = a;
        if (cambiaApertura)
        {
            await AbrirPorIdAsync(cut, c);
            abierta = c;
            await cut.Find(".composer-nota-interna textarea").InputAsync(new ChangeEventArgs { Value = "Nota de C a medias." });
        }
        var detallesAntes = DetallesPedidos(mediador, abierta.Id);

        await cut.InvokeAsync(() => aprobacion.SetResult(Result.Exito()));
        await envio;
        cut.Render();

        DetallesPedidos(mediador, abierta.Id).Should().Be(detallesAntes + (cambiaApertura ? 0 : 1),
            "con la misma apertura se recarga el hilo con el recordatorio enviado (control); con otra conversación abierta, no");
        if (cambiaApertura)
            cut.FindComponent<ComposerNotaInterna>().Instance.TieneTextoSinGuardar.Should().BeTrue("la nota interna de C sigue escrita");
    }

    /// <summary>
    /// Revisión Codex r7 de la #2167, MEDIO 4: con «Actualizar documentación» cargado, una recarga cambia el Cliente empresarial.
    /// Sin cambios del Gestor CAE el modal se cierra y se avisa (control); con cambios, no se cierra en silencio: sigue abierto
    /// con lo escrito, «Aplicar» bloqueado con el motivo y el aviso a la vista, y aplicar no envía nada. Detecta quitar la
    /// excepción de <c>CambiarContexto</c> o el bloqueo de <c>AplicarActualizacionDocumentoAsync</c>.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Una_recarga_que_cambia_el_Cliente_empresarial_no_cierra_en_silencio_Actualizar_documentacion_editado(bool conCambios)
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => ConAdjuntoTc2(DetalleDe(conversacion)) };
        escenario.Conversaciones.Add(conversacion);
        var tipoId = Guid.NewGuid();
        var empresaId = Guid.NewGuid();
        escenario.Interceptar = peticion => peticion switch
        {
            ObtenerEmpresasParaSelectorQuery => Ya(new List<EmpresaSelectorDto> { new(empresaId, "Instalaciones Norte S.L.") }),
            DetectarActualizacionDocumentoDesdeAdjuntoQuery => Ya(Result.Exito(new DeteccionActualizacionDocumentoDto(
                AdjuntoId, tipoId, null, CaeManager.Domain.Documentos.AmbitoAplicacion.Empresa, null, null, empresaId, null, null, null, 80))),
            _ => null,
        };
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");
        await cut.Find(".timeline-adjunto-actualizar-documento").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".modal-actualizar-documento-formulario").Should().ContainSingle("el modal está cargado"));
        if (conCambios)
            await cut.Find(".modal-actualizar-documento-formulario textarea").InputAsync(new ChangeEventArgs { Value = "Renovado en septiembre." });

        escenario.Detalle = _ => ConAdjuntoTc2(DetalleConEbro(conversacion));
        await RecibirMensajeAsync(cut, conversacion.Id);
        cut.WaitForAssertion(() => cut.Find(".bandeja-cliente-identidad").TextContent.Should().Contain(ClienteEbro.RazonSocial));
        const string motivo = "La conversación se ha actualizado y este formulario ya no corresponde a sus datos actuales";

        if (!conCambios)
        {
            cut.FindAll(".modal-actualizar-documento-formulario").Should().BeEmpty("sin cambios, el modal del contexto anterior se cierra");
            HayToast(this, "La conversación se ha actualizado y se ha cerrado «Actualizar documentación»").Should().BeTrue();
            return;
        }

        cut.FindAll(".modal-actualizar-documento-formulario").Should().ContainSingle("con lo escrito, el modal no se cierra en silencio");
        CampoPrivado<string>(cut, "_comentariosDocumentoFormulario").Should().Be("Renovado en septiembre.", "lo escrito se conserva para copiarlo");
        HayToast(this, "La conversación se ha actualizado y se ha cerrado «Actualizar documentación»").Should().BeFalse("no se ha cerrado");
        var aplicar = cut.FindAll(".modal-pie button").Single(b => b.TextContent.Contains("Aplicar y registrar"));
        aplicar.HasAttribute("disabled").Should().BeTrue("«Aplicar» se bloquea: el formulario es del contexto anterior");
        aplicar.GetAttribute("title").Should().StartWith(motivo);
        // El title del botón ya lleva el mismo texto: se busca el aviso como componente para que quitar Aviso= lo detecte.
        cut.FindComponents<AvisoFormulario>().Should().Contain(a => a.Instance.Mensaje != null && a.Instance.Mensaje.StartsWith(motivo),
            "el aviso está a la vista dentro del modal");

        await InvocarPrivado(cut, "AplicarActualizacionDocumentoAsync");
        mediador.Enviados.OfType<ActualizarDocumentoDesdeAdjuntoCommand>().Should().BeEmpty("aplicar por otra vía tampoco envía nada");

        // Al cerrarlo (descartando lo escrito) y reabrirlo, el formulario vuelve a poder aplicarse con los datos actuales.
        await PulsarCancelarEnAsync(cut, ".modal-pie");
        await cut.ComprobarQuePreguntaYDescartarAsync(".modal-actualizar-documento-formulario");
        await cut.Find(".timeline-adjunto-actualizar-documento").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".modal-actualizar-documento-formulario").Should().ContainSingle());
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Contains("Aplicar y registrar")).HasAttribute("disabled").Should().BeFalse();
    }

    /// <summary>
    /// Inventario de la r6 de la #2167, camino no señalado (cambio de estado A→B→A): «Resuelta» está en vuelo, el Gestor CAE abre
    /// otra conversación y vuelve a la primera. Al terminar no se salta a la sucesora desde la reapertura. Detecta volver a comparar
    /// solo el id de la conversación en <c>CambiarEstadoAsync</c>.
    /// </summary>
    [Fact]
    public async Task Resolver_en_vuelo_y_reabrir_la_misma_conversacion_no_salta_a_la_sucesora()
    {
        var (escenario, _, _) = DosConversacionesConPlantilla(PlantillaCertificado());
        var cambio = Retenida();
        escenario.Interceptar = p => p is CambiarEstadoConversacionCommand ? cambio.Task : null;
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");

        var resolucion = SelectorCambiarEstado(cut).ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoConversacion.Resuelta) });
        cut.WaitForAssertion(() => mediador.Enviados.OfType<CambiarEstadoConversacionCommand>().Should().ContainSingle("el cambio está en vuelo"));
        await SeleccionarFila(cut, "Segunda conversación");
        cut.WaitForAssertion(() => TituloAbierto(cut).Should().Be("Segunda conversación"));
        await SeleccionarFila(cut, "Primera conversación");
        cut.WaitForAssertion(() => TituloAbierto(cut).Should().Be("Primera conversación"));

        await cut.InvokeAsync(() => cambio.SetResult(Result.Exito()));
        await resolucion;
        cut.Render();

        TituloAbierto(cut).Should().Be("Primera conversación", "la reapertura no es la apertura que pidió resolver");
    }

    /// <summary>
    /// Inventario de la r6 de la #2167, camino no señalado (vista «Mi buzón personal»): mientras se carga el buzón llega un aviso de
    /// tiempo real. El refresco recarga el buzón, no la lista de Clientes, y el buzón se muestra. Detecta quitar la delegación de
    /// <c>CargarListaAsync</c> a <c>CargarBuzonPersonalAsync</c>.
    /// </summary>
    [Fact]
    public async Task Un_aviso_de_tiempo_real_durante_la_carga_del_buzon_personal_no_lo_deja_vacio()
    {
        var escenario = new Escenario();
        escenario.BuzonPersonal.Add(new MensajeBuzonPersonalDto(Guid.NewGuid(), Guid.NewGuid(), "Aviso del buzón personal",
            "plataforma@example.invalid", "<p>Aviso.</p>", DateTime.UtcNow, true, null, default));
        var primeraCarga = Retenida();
        var lecturas = 0;
        escenario.Interceptar = p => p is ObtenerMensajesBuzonPersonalQuery && Interlocked.Increment(ref lecturas) == 1 ? primeraCarga.Task : null;
        var (cut, mediador) = Renderizar(escenario, "comunicaciones?vista=personal");
        cut.WaitForAssertion(() => Volatile.Read(ref lecturas).Should().Be(1, "la primera carga del buzón está en vuelo"));

        await cut.InvokeAsync(() => RefrescoDeFondo(cut));
        await cut.InvokeAsync(() => primeraCarga.SetResult(escenario.BuzonPersonal.ToList()));
        cut.Render();

        mediador.Enviados.OfType<ObtenerConversacionesQuery>().Should().BeEmpty("en «Mi buzón personal» el refresco no pide la lista de Clientes");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Aviso del buzón personal"));
    }

    // ---------------------------------------------------------------- revisión Codex r6 de la #2167, ronda 2: una prueba por guarda
    //
    // Patrón de cada prueba: la operación se lanza y su respuesta se retiene; se abre otra conversación (o no: el control con la
    // misma apertura demuestra que el instrumento ve el efecto cuando la guarda lo permite); se libera la respuesta y se mide
    // lo que la guarda decide. Los métodos privados se invocan igual que lo hacen los componentes hijos (callbacks de la página).

    private static Task InvocarPrivado(IRenderedComponent<Bandeja> cut, string metodo, params object?[] argumentos) =>
        cut.InvokeAsync(() => (Task)typeof(Bandeja).GetMethod(metodo, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cut.Instance, argumentos)!);

    private static void FijarCampoPrivado(IRenderedComponent<Bandeja> cut, string nombre, object? valor) =>
        typeof(Bandeja).GetField(nombre, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cut.Instance, valor);

    /// <summary>Abre otra conversación sin pasar por la lista (que puede estar cargándose); si pregunta por lo escrito, descarta.</summary>
    private static async Task AbrirPorIdAsync(IRenderedComponent<Bandeja> cut, ConversacionListaDto conversacion)
    {
        var seleccion = InvocarPrivado(cut, "SeleccionarConversacionAsync", conversacion.Id);
        cut.WaitForState(() => seleccion.IsCompleted || cut.FindAll(".modal-pie button").Any(b => b.TextContent.Trim() == "Salir y descartar"));
        if (!seleccion.IsCompleted) await cut.PulsarEnElAvisoAsync("Salir y descartar");
        await seleccion;
        cut.WaitForAssertion(() => TituloAbierto(cut).Should().Be(conversacion.Asunto));
    }

    private static int DetallesPedidos(MediadorControlado mediador, Guid conversacionId) =>
        mediador.Enviados.OfType<ObtenerConversacionPorIdQuery>().Count(q => q.Id == conversacionId);

    private static int ListasPedidas(MediadorControlado mediador) => mediador.Enviados.OfType<ObtenerConversacionesQuery>().Count();

    private static (Escenario Escenario, ConversacionListaDto A, ConversacionListaDto B, ConversacionListaDto C) TresConversaciones()
    {
        var escenario = new Escenario();
        var a = Conversacion("Conversación A", ClienteRefrielectric);
        var b = Conversacion("Conversación B", ClienteRefrielectric, "laura.gil@refrielectric.es");
        var c = Conversacion("Conversación C", ClienteRefrielectric, "pedro.sanz@refrielectric.es");
        escenario.Conversaciones.AddRange([a, b, c]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(x => x.Id == id));
        return (escenario, a, b, c);
    }

    /// <summary>El límite de errores de la interfaz contiene la excepción de un componente y la registra aquí: en la prueba, la relanza.</summary>
    private sealed class AlertaQueRelanza : IAlertaOperativa
    {
        public void Emitir(string mensaje, NivelAlertaOperativa nivel) { }

        public void DejarMigaDePan(string mensaje) { }

        public IDisposable IniciarAmbitoDeCaptura() => new Ambito();

        private sealed class Ambito : IDisposable
        {
            public void Dispose() { }
        }

        public void CapturarExcepcion(Exception excepcion) =>
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(excepcion).Throw();
    }

    private sealed record EfectoTrasLiberar(int DetallesDeLaAbierta, int Listas, string Titulo);

    /// <summary>
    /// Lanza una acción sobre A, retiene la respuesta de su comando, abre C si <paramref name="cambiaApertura"/> y libera la
    /// respuesta. Devuelve lo que la acción hizo DESPUÉS de liberarla: cuántas veces recargó el detalle de la conversación abierta
    /// y la lista, y cuál quedó abierta.
    /// </summary>
    private async Task<EfectoTrasLiberar> LiberarAccionEnVueloAsync(
        Func<object, bool> esElComando, object respuesta,
        Func<IRenderedComponent<Bandeja>, ConversacionListaDto, ConversacionListaDto, Task> lanzar, bool cambiaApertura)
    {
        var (escenario, a, b, c) = TresConversaciones();
        var retenida = Retenida();
        escenario.Interceptar = p => esElComando(p) ? retenida.Task : null;
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, a.Asunto);

        var accion = lanzar(cut, a, b);
        cut.WaitForAssertion(() => mediador.Enviados.Any(esElComando).Should().BeTrue("la acción está en vuelo"));
        var abierta = a;
        if (cambiaApertura)
        {
            await AbrirPorIdAsync(cut, c);
            abierta = c;
        }
        var detallesAntes = DetallesPedidos(mediador, abierta.Id);
        var listasAntes = ListasPedidas(mediador);

        await cut.InvokeAsync(() => retenida.SetResult(respuesta));
        await accion;
        cut.Render();

        return new EfectoTrasLiberar(DetallesPedidos(mediador, abierta.Id) - detallesAntes, ListasPedidas(mediador) - listasAntes, TituloAbierto(cut));
    }

    /// <summary>
    /// <c>CambiarPrioridadAsync</c>: <c>if (MismaApertura) CargarDetalleAsync()</c>. Con otra conversación abierta, el detalle que se
    /// pintaba no cambió: solo se recarga la lista. Si la guarda se quitara, tras abrir C se recargaría el detalle de C.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cambiar_la_prioridad_con_otra_conversacion_abierta_solo_recarga_la_lista(bool cambiaApertura)
    {
        var efecto = await LiberarAccionEnVueloAsync(p => p is CambiarPrioridadConversacionCommand, Result.Exito(),
            (cut, _, _) => InvocarPrivado(cut, "CambiarPrioridadAsync", nameof(PrioridadConversacion.Urgente)), cambiaApertura);

        efecto.Listas.Should().Be(1, "la lista se recarga siempre");
        efecto.DetallesDeLaAbierta.Should().Be(cambiaApertura ? 0 : 1,
            "con la misma apertura se recarga el detalle (control); con otra conversación abierta, no");
    }

    /// <summary>
    /// <c>AsignarClienteTriageAsync</c>: <c>if (MismaApertura) CargarDetalleAsync()</c>. Misma estructura que la prioridad: con otra
    /// conversación abierta solo se recarga la lista (y el aviso de éxito sale igualmente).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Asignar_el_Cliente_en_triage_con_otra_conversacion_abierta_solo_recarga_la_lista(bool cambiaApertura)
    {
        var efecto = await LiberarAccionEnVueloAsync(p => p is AsignarClienteConversacionCommand, Result.Exito(),
            (cut, _, _) =>
            {
                FijarCampoPrivado(cut, "_clienteTriageSeleccionado", ClienteEbroId.ToString());
                return InvocarPrivado(cut, "AsignarClienteTriageAsync");
            }, cambiaApertura);

        HayToast(this, "Conversación asignada al Cliente.").Should().BeTrue("el aviso de éxito sale con cualquier apertura");
        efecto.Listas.Should().Be(1, "la lista se recarga siempre");
        efecto.DetallesDeLaAbierta.Should().Be(cambiaApertura ? 0 : 1);
    }

    /// <summary>
    /// <c>DescartarSugerenciaVisitaAsync</c>: <c>if (MismaApertura &amp;&amp; …) CargarDetalleAsync()</c>. Descartar una sugerencia de la
    /// conversación abandonada no recarga la que se abrió después.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Descartar_una_sugerencia_de_visita_con_otra_conversacion_abierta_no_recarga_su_detalle(bool cambiaApertura)
    {
        var efecto = await LiberarAccionEnVueloAsync(p => p is DescartarSugerenciaVisitaCorreoCommand, Result.Exito(),
            (cut, _, _) => InvocarPrivado(cut, "DescartarSugerenciaVisitaAsync", Guid.NewGuid()), cambiaApertura);

        efecto.DetallesDeLaAbierta.Should().Be(cambiaApertura ? 0 : 1);
    }

    /// <summary>Ídem para <c>DescartarSugerenciaGestionAsync</c>.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Descartar_una_sugerencia_de_gestion_con_otra_conversacion_abierta_no_recarga_su_detalle(bool cambiaApertura)
    {
        var efecto = await LiberarAccionEnVueloAsync(p => p is DescartarSugerenciaGestionCorreoCommand, Result.Exito(),
            (cut, _, _) => InvocarPrivado(cut, "DescartarSugerenciaGestionAsync", Guid.NewGuid()), cambiaApertura);

        efecto.DetallesDeLaAbierta.Should().Be(cambiaApertura ? 0 : 1);
    }

    /// <summary>Ídem para <c>GenerarGestionesDesdeSugerenciaAsync</c> (el aviso con el número de gestiones sale igualmente).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generar_gestiones_desde_una_sugerencia_con_otra_conversacion_abierta_no_recarga_su_detalle(bool cambiaApertura)
    {
        var efecto = await LiberarAccionEnVueloAsync(p => p is CrearGestionesParaTrabajadorCommand, Result.Exito(new ResultadoCrearGestionesDto(2)),
            (cut, _, _) => InvocarPrivado(cut, "GenerarGestionesDesdeSugerenciaAsync", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), cambiaApertura);

        HayToast(this, "Se generaron 2 gestión(es).").Should().BeTrue("el aviso de éxito sale con cualquier apertura");
        efecto.DetallesDeLaAbierta.Should().Be(cambiaApertura ? 0 : 1);
    }

    /// <summary>
    /// <c>ConfirmarRuidoAsync</c>: <c>if (MismaApertura) CargarDetalleAsync()</c>; la lista se recarga siempre.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirmar_un_mensaje_de_ruido_con_otra_conversacion_abierta_solo_recarga_la_lista(bool cambiaApertura)
    {
        var efecto = await LiberarAccionEnVueloAsync(p => p is ConfirmarClasificacionRuidoMensajeCommand, Result.Exito(),
            (cut, _, _) => InvocarPrivado(cut, "ConfirmarRuidoAsync", Guid.NewGuid()), cambiaApertura);

        efecto.Listas.Should().Be(1, "la lista se recarga siempre");
        efecto.DetallesDeLaAbierta.Should().Be(cambiaApertura ? 0 : 1);
    }

    /// <summary>
    /// <c>VincularConversacionAsync</c>: <c>if (MismaApertura) SeleccionarConversacionAsync(destino)</c>. Con otra conversación abierta,
    /// vincular no arrastra al Gestor CAE al destino: se queda en la que eligió (la lista se recarga y el aviso sale igualmente).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Vincular_con_otra_conversacion_abierta_no_lleva_al_destino(bool cambiaApertura)
    {
        var efecto = await LiberarAccionEnVueloAsync(p => p is VincularConversacionCommand, Result.Exito(),
            (cut, _, b) => InvocarPrivado(cut, "VincularConversacionAsync", b.Id), cambiaApertura);

        HayToast(this, "Conversaciones vinculadas.").Should().BeTrue("el aviso de éxito sale con cualquier apertura");
        efecto.Listas.Should().Be(1, "la lista se recarga siempre");
        efecto.Titulo.Should().Be(cambiaApertura ? "Conversación C" : "Conversación B",
            "con la misma apertura se abre el destino (control); con otra, se respeta la elegida");
    }

    /// <summary>
    /// <c>SiguienteConversacionAsync</c>: tras cargar la página siguiente, <c>if (cargada &amp;&amp; MismaApertura) Seleccionar(primera)</c>.
    /// Con la carga en vuelo y otra conversación abierta, no se salta a la primera de la página nueva.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Siguiente_con_la_pagina_en_vuelo_y_otra_conversacion_abierta_no_salta_a_la_primera_de_la_pagina(bool cambiaApertura)
    {
        var escenario = new Escenario();
        var conversaciones = Enumerable.Range(1, 21).Select(i => Conversacion($"Conversación {i:00}", ClienteRefrielectric)).ToList();
        escenario.Conversaciones.AddRange(conversaciones);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(x => x.Id == id));
        var lista = Retenida();
        var retener = false;
        ObtenerConversacionesQuery? pedida = null;
        escenario.Interceptar = p =>
        {
            if (!retener || p is not ObtenerConversacionesQuery consulta) return null;
            pedida = consulta;
            retener = false;
            return lista.Task;
        };
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Conversación 20");

        retener = true;
        var siguiente = InvocarPrivado(cut, "SiguienteConversacionAsync");
        cut.WaitForAssertion(() => pedida.Should().NotBeNull("la página siguiente está en vuelo"));
        pedida!.Pagina.Should().Be(2);
        if (cambiaApertura) await AbrirPorIdAsync(cut, conversaciones[0]);

        await cut.InvokeAsync(() => lista.SetResult(escenario.Pagina(pedida!)));
        await siguiente;
        cut.Render();

        TituloAbierto(cut).Should().Be(cambiaApertura ? "Conversación 01" : "Conversación 21",
            "con la misma apertura se abre la primera de la página nueva (control); con otra, se respeta la elegida");
    }

    /// <summary>
    /// <c>CambiarEstadoAsync</c>, 2.ª comprobación (tras <c>RegistrarAccionMedidaAsync</c>): el medidor cierra el tramo mientras el Gestor
    /// CAE abre otra conversación. No se recarga el detalle de la abierta ni se sigue a la lista fresca: solo la lista. Si la
    /// comprobación se quitara, tras abrir C se recargaría el detalle de C.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolver_con_el_medidor_en_vuelo_y_otra_conversacion_abierta_solo_recarga_la_lista(bool cambiaApertura)
    {
        var (escenario, a, _, c) = TresConversaciones();
        Services.AddSingleton<IAlertaOperativa>(new AlertaQueRelanza());
        var tramo = Retenida();
        escenario.Interceptar = p => p switch
        {
            ObtenerTiempoGestionConversacionQuery => Ya(new TiempoGestionConversacionDto(true, 0, 3600)),
            RegistrarTramoGestionCommand => tramo.Task,
            _ => null
        };
        var (cut, mediador) = Renderizar(escenario, "comunicaciones?estado=Abierta", medidorActivo: true);
        await SeleccionarFila(cut, a.Asunto);
        // El tramo del medidor cuenta segundos enteros desde que se abrió la conversación: sin ellos no registra nada.
        await Task.Delay(1200);

        var resolucion = SelectorCambiarEstado(cut).ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoConversacion.Resuelta) });
        cut.WaitForAssertion(() => mediador.Enviados.OfType<RegistrarTramoGestionCommand>().Should().NotBeEmpty("el medidor está cerrando el tramo"));
        var abierta = a;
        if (cambiaApertura)
        {
            await AbrirPorIdAsync(cut, c);
            abierta = c;
        }
        var detallesAntes = DetallesPedidos(mediador, abierta.Id);
        var listasAntes = ListasPedidas(mediador);

        await cut.InvokeAsync(() => tramo.SetResult(Result.Exito()));
        await resolucion;
        cut.Render();

        DetallesPedidos(mediador, abierta.Id).Should().Be(detallesAntes + (cambiaApertura ? 0 : 1),
            "con la misma apertura se recarga el detalle (control); con otra conversación abierta, no");
        if (cambiaApertura) ListasPedidas(mediador).Should().Be(listasAntes + 1, "solo se recarga la lista, sin la recarga fresca");
    }

    /// <summary>
    /// <c>CambiarEstadoAsync</c>, 1.ª comprobación (justo tras el comando): el Gestor CAE abre otra conversación mientras el
    /// comando viaja. El medidor de la abierta NO cierra su tramo por una acción que fue de otra apertura: no se envía
    /// <c>RegistrarTramoGestionCommand</c>. Si la comprobación se quitara, se registraría el tramo de la conversación abierta
    /// (la 2.ª comprobación solo vería la apertura cambiada después de haber cerrado el tramo).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolver_con_otra_conversacion_abierta_antes_de_volver_el_comando_no_cierra_el_tramo_del_medidor(bool cambiaApertura)
    {
        var (escenario, a, _, c) = TresConversaciones();
        Services.AddSingleton<IAlertaOperativa>(new AlertaQueRelanza());
        var comando = Retenida();
        escenario.Interceptar = p => p switch
        {
            ObtenerTiempoGestionConversacionQuery => Ya(new TiempoGestionConversacionDto(true, 0, 3600)),
            CambiarEstadoConversacionCommand => comando.Task,
            RegistrarTramoGestionCommand => Ya(Result.Exito()),
            _ => null
        };
        var (cut, mediador) = Renderizar(escenario, "comunicaciones?estado=Abierta", medidorActivo: true);
        await SeleccionarFila(cut, a.Asunto);
        // El tramo del medidor cuenta segundos enteros desde que se abrió la conversación: sin ellos no registra nada.
        await Task.Delay(1200);

        var resolucion = SelectorCambiarEstado(cut).ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoConversacion.Resuelta) });
        cut.WaitForAssertion(() => mediador.Enviados.OfType<CambiarEstadoConversacionCommand>().Should().NotBeEmpty("el comando está en vuelo"));
        if (cambiaApertura)
        {
            // Salir de A cierra su tramo por su cuenta; C necesita sus propios segundos para que, sin la guarda, hubiese tramo que registrar.
            await AbrirPorIdAsync(cut, c);
            await Task.Delay(1200);
        }
        var tramosAntes = mediador.Enviados.OfType<RegistrarTramoGestionCommand>().Count();

        await cut.InvokeAsync(() => comando.SetResult(Result.Exito()));
        await resolucion;
        cut.Render();

        mediador.Enviados.OfType<RegistrarTramoGestionCommand>().Count().Should().Be(tramosAntes + (cambiaApertura ? 0 : 1),
            "con la misma apertura el medidor cierra el tramo (control); con otra conversación abierta, el comando de la anterior no cierra el suyo");
    }

    /// <summary>
    /// <c>CambiarEstadoAsync</c>, 3.ª comprobación (tras la lista fresca): la lista está en vuelo y el Gestor CAE abre otra
    /// conversación. No se salta a la sucesora de la resuelta. Si la comprobación se quitara, se abriría B sobre C.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolver_con_la_lista_en_vuelo_y_otra_conversacion_abierta_no_salta_a_la_sucesora(bool cambiaApertura)
    {
        var (escenario, a, b, c) = TresConversaciones();
        var lista = Retenida();
        var retener = false;
        ObtenerConversacionesQuery? pedida = null;
        escenario.Interceptar = p =>
        {
            if (!retener || p is not ObtenerConversacionesQuery consulta) return null;
            pedida = consulta;
            retener = false;
            return lista.Task;
        };
        var (cut, _) = Renderizar(escenario, "comunicaciones?estado=Abierta");
        await SeleccionarFila(cut, a.Asunto);

        retener = true;
        var resolucion = SelectorCambiarEstado(cut).ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoConversacion.Resuelta) });
        cut.WaitForAssertion(() => pedida.Should().NotBeNull("la lista fresca está en vuelo"));
        if (cambiaApertura) await AbrirPorIdAsync(cut, c);

        await cut.InvokeAsync(() => lista.SetResult(escenario.Pagina(pedida!)));
        await resolucion;
        cut.Render();

        TituloAbierto(cut).Should().Be(cambiaApertura ? c.Asunto : b.Asunto,
            "con la misma apertura se salta a la sucesora (control); con otra conversación abierta, se respeta la elegida");
    }

    // --- Actualizar documentación: la solicitud del modal (AplicarActualizacionDocumentoAsync) ---

    private static (Escenario Escenario, ConversacionListaDto A, ConversacionListaDto B, ConversacionListaDto C) TresConversacionesConAdjunto()
    {
        var tres = TresConversaciones();
        tres.Escenario.Detalle = id => ConAdjuntoTc2(DetalleDe(tres.Escenario.Conversaciones.Single(x => x.Id == id)));
        return tres;
    }

    private static Task<object?>? InterceptarDocumento(object peticion) => peticion switch
    {
        ObtenerEmpresasParaSelectorQuery => Ya(new List<EmpresaSelectorDto> { new(Guid.NewGuid(), "Instalaciones Norte S.L.") }),
        DetectarActualizacionDocumentoDesdeAdjuntoQuery => Ya(Result.Exito(new DeteccionActualizacionDocumentoDto(
            AdjuntoId, null, null, CaeManager.Domain.Documentos.AmbitoAplicacion.Empresa, null, null, null, null, null, null, 80))),
        _ => null,
    };

    /// <summary>Abre «Actualizar documentación» sobre el adjunto de la conversación abierta y rellena el formulario con datos válidos.</summary>
    private static async Task AbrirYRellenarModalDocumentoAsync(IRenderedComponent<Bandeja> cut)
    {
        await cut.Find(".timeline-adjunto-actualizar-documento").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".modal-actualizar-documento-formulario").Should().ContainSingle("el modal está cargado"));
        FijarCampoPrivado(cut, "_tipoDocumentoIdFormulario", Guid.NewGuid().ToString());
        FijarCampoPrivado(cut, "_propietarioDocumentoFormulario", "Empresa");
        FijarCampoPrivado(cut, "_empresaDocumentoIdFormulario", Guid.NewGuid().ToString());
        FijarCampoPrivado(cut, "_fechaEmisionDocumentoFormulario", "2026-11-01");
    }

    /// <summary>
    /// Deja una actualización de documentación en vuelo sobre A. Con <paramref name="reabre"/>, abre C (que cierra el modal de A) y
    /// vuelve a abrir el modal sobre el adjunto de C: la respuesta pendiente es de una apertura anterior del modal.
    /// </summary>
    private async Task<(IRenderedComponent<Bandeja> Cut, MediadorControlado Mediador, Task Aplicacion, ConversacionListaDto Abierta, List<TaskCompletionSource<object?>> Comandos)>
        AplicacionDeDocumentoEnVueloAsync(bool reabre)
    {
        var (escenario, a, _, c) = TresConversacionesConAdjunto();
        var comandos = new List<TaskCompletionSource<object?>>();
        escenario.Interceptar = p =>
        {
            if (p is not ActualizarDocumentoDesdeAdjuntoCommand) return InterceptarDocumento(p);
            var retenida = Retenida();
            lock (comandos) comandos.Add(retenida);
            return retenida.Task;
        };
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, a.Asunto);
        await AbrirYRellenarModalDocumentoAsync(cut);

        var aplicacion = InvocarPrivado(cut, "AplicarActualizacionDocumentoAsync");
        cut.WaitForAssertion(() => mediador.Enviados.OfType<ActualizarDocumentoDesdeAdjuntoCommand>().Should().ContainSingle("la actualización está en vuelo"));
        var abierta = a;
        if (reabre)
        {
            await AbrirPorIdAsync(cut, c);
            abierta = c;
            await AbrirYRellenarModalDocumentoAsync(cut);
        }
        return (cut, mediador, aplicacion, abierta, comandos);
    }

    /// <summary>
    /// <c>AplicarActualizacionDocumentoAsync</c>, error: <c>if (solicitud == _solicitudActualizarDocumentoVigente) _errorActualizarDocumento = …</c>.
    /// El fallo de una apertura anterior del modal no aparece en el modal reabierto sobre otra conversación.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task El_fallo_de_actualizar_la_documentacion_de_una_apertura_anterior_no_sale_en_el_modal_reabierto(bool reabre)
    {
        var (cut, _, aplicacion, _, comandos) = await AplicacionDeDocumentoEnVueloAsync(reabre);

        await cut.InvokeAsync(() => comandos[0].SetResult(Result.Fallo<Guid>(Error.Crear("Prueba.Fallo", "Fallo del servidor al actualizar."))));
        await aplicacion;
        cut.Render();

        cut.FindAll(".modal-actualizar-documento-formulario").Should().ContainSingle("control: el modal sigue abierto en ambos casos");
        if (reabre) cut.Markup.Should().NotContain("Fallo del servidor al actualizar.", "el fallo era de la apertura anterior del modal");
        else cut.Markup.Should().Contain("Fallo del servidor al actualizar.", "control: con la misma apertura, el modal dice el motivo");
    }

    /// <summary>
    /// <c>AplicarActualizacionDocumentoAsync</c>, éxito: el cierre del modal (<c>solicitud == vigente</c>) y la recarga
    /// (<c>MismaApertura</c>). Con el modal reabierto sobre C, ni se cierra el nuevo ni se recarga el detalle de C; el aviso de éxito
    /// sale igualmente.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Una_actualizacion_de_documentacion_de_una_apertura_anterior_no_cierra_el_modal_reabierto_ni_recarga_el_detalle(bool reabre)
    {
        var (cut, mediador, aplicacion, abierta, comandos) = await AplicacionDeDocumentoEnVueloAsync(reabre);
        var detallesAntes = DetallesPedidos(mediador, abierta.Id);

        await cut.InvokeAsync(() => comandos[0].SetResult(Result.Exito(Guid.NewGuid())));
        await aplicacion;
        cut.Render();

        HayToast(this, "Documentación actualizada.").Should().BeTrue("el aviso de éxito sale con cualquier apertura");
        cut.FindAll(".modal-actualizar-documento-formulario").Should().HaveCount(reabre ? 1 : 0,
            "con la misma apertura se cierra (control); el modal reabierto es de otra apertura y sigue abierto");
        DetallesPedidos(mediador, abierta.Id).Should().Be(detallesAntes + (reabre ? 0 : 1));
    }

    /// <summary>
    /// <c>AplicarActualizacionDocumentoAsync</c>, <c>finally</c>: <c>if (!_modalActualizarDocumentoVisible || solicitud == vigente) _aplicandoDocumento = false</c>.
    /// Con el modal reabierto y aplicando su propia actualización, la que termina de la apertura anterior no apaga el indicador.
    /// </summary>
    [Fact]
    public async Task Una_actualizacion_de_una_apertura_anterior_no_apaga_el_indicador_de_la_del_modal_reabierto()
    {
        var (cut, mediador, primera, _, comandos) = await AplicacionDeDocumentoEnVueloAsync(reabre: true);
        var segunda = InvocarPrivado(cut, "AplicarActualizacionDocumentoAsync");
        cut.WaitForAssertion(() => mediador.Enviados.OfType<ActualizarDocumentoDesdeAdjuntoCommand>().Should().HaveCount(2, "la del modal reabierto está en vuelo"));
        CampoPrivado<bool>(cut, "_aplicandoDocumento").Should().BeTrue("control positivo: el modal reabierto está aplicando");

        await cut.InvokeAsync(() => comandos[0].SetResult(Result.Exito(Guid.NewGuid())));
        await primera;
        cut.Render();

        CampoPrivado<bool>(cut, "_aplicandoDocumento").Should().BeTrue("la actualización que terminó era de la apertura anterior");
        await cut.InvokeAsync(() => comandos[1].SetResult(Result.Exito(Guid.NewGuid())));
        await segunda;
        cut.Render();
        CampoPrivado<bool>(cut, "_aplicandoDocumento").Should().BeFalse("control: la propia solicitud sí apaga su indicador");
    }

    // --- Pedir prioridad: la solicitud del Drawer (EnviarPrioridadAsync) ---

    private static async Task ElegirCentroYAbrirDrawerPrioridadAsync(IRenderedComponent<Bandeja> cut)
    {
        cut.WaitForAssertion(() => cut.FindAll(".composer-correo-prellenados select").Should().ContainSingle());
        await cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Pedir prioridad de validación").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "Prioridad Centro Norte"));
    }

    /// <summary>
    /// <c>EnviarPrioridadAsync</c>: tras la espera, el error (resultado fallido y <see cref="ValidationException"/>) y el cierre del
    /// Drawer solo se escriben si <c>solicitud == _solicitudPrioridadVigente</c>. El Gestor CAE abre B y vuelve a abrir el Drawer
    /// mientras viaja el envío de A: el desenlace del envío viejo no cierra ni ensucia el Drawer nuevo. Cada desenlace cubre una
    /// línea distinta de la guarda.
    /// </summary>
    [Theory]
    [InlineData("exito", false)]
    [InlineData("exito", true)]
    [InlineData("fallo", false)]
    [InlineData("fallo", true)]
    [InlineData("validacion", false)]
    [InlineData("validacion", true)]
    public async Task El_desenlace_de_pedir_prioridad_de_una_apertura_anterior_no_toca_el_Drawer_reabierto(string desenlace, bool reabre)
    {
        var (escenario, a, b, _) = TresConversaciones();
        var envio = Retenida();
        escenario.Interceptar = p => p is PedirPrioridadValidacionCommand ? envio.Task : CentrosPorCliente(p);
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, a.Asunto);
        await ElegirCentroYAbrirDrawerPrioridadAsync(cut);

        var envioPrimero = Boton(cut, ".drawer-pie button", "Enviar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => mediador.Enviados.OfType<PedirPrioridadValidacionCommand>().Should().ContainSingle("el envío está en vuelo"));
        if (reabre)
        {
            await AbrirPorIdAsync(cut, b);
            await ElegirCentroYAbrirDrawerPrioridadAsync(cut);
        }

        await cut.InvokeAsync(() =>
        {
            switch (desenlace)
            {
                case "exito": envio.SetResult(Result.Exito()); break;
                case "fallo": envio.SetResult(Result.Fallo(Error.Crear("Prueba.Fallo", "Fallo del servidor al pedir prioridad."))); break;
                default: envio.SetException(new ValidationException([new ValidationFailure("Asunto", "Asunto demasiado largo.")])); break;
            }
        });
        await envioPrimero;
        cut.Render();

        if (desenlace == "exito")
            CampoPrivado<bool>(cut, "_drawerPrioridadVisible").Should().Be(reabre,
                "con la misma apertura se cierra (control); el Drawer reabierto es de otra apertura y sigue abierto");
        else
            CampoPrivado<string?>(cut, "_mensajeErrorPrioridad").Should().Be(
                reabre ? null : desenlace == "fallo" ? "Fallo del servidor al pedir prioridad." : "Asunto demasiado largo.",
                "con la misma apertura el Drawer dice el motivo (control); el reabierto no recibe el error de la apertura anterior");
    }

    // --- Aviso de adjuntos (LimpiarLoEnviado) ---

    private sealed class ArchivoQueNoCabe : IBrowserFile
    {
        public string Name => "enorme.pdf";
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => long.MaxValue;
        public string ContentType => "application/pdf";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Un archivo que no cabe no se abre.");
    }

    private static Task AdjuntarVariosAsync(IRenderedComponent<Bandeja> cut, IReadOnlyList<IBrowserFile> archivos) =>
        cut.InvokeAsync(() => cut.FindComponent<ComposerBar>().Instance.OnArchivosSeleccionados.InvokeAsync(new InputFileChangeEventArgs(archivos)));

    /// <summary>
    /// <c>LimpiarLoEnviado</c>: <c>if (enviado.ErrorAdjuntos is not null &amp;&amp; _errorAdjuntos == enviado.ErrorAdjuntos) _errorAdjuntos = null</c>.
    /// El aviso que acompañaba al borrador enviado se retira; el aviso nuevo que salió mientras viajaba el envío se conserva.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task El_aviso_de_adjuntos_del_borrador_enviado_se_retira_y_el_nuevo_se_conserva(bool cambiaElAviso)
    {
        var (escenario, a, _, _) = TresConversaciones();
        var envio = Retenida();
        escenario.Interceptar = p => p is ResponderConversacionCommand ? envio.Task : null;
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, a.Asunto);
        await Escribir(cut, "Respuesta con archivos de más.");
        await AdjuntarVariosAsync(cut, Enumerable.Range(0, CaeManager.Web.Features.Comunicaciones.AdjuntosCorreo.MaximoArchivos + 1)
            .Select(i => (IBrowserFile)ArchivoListo($"f{i}.pdf")).ToList());
        var avisoDelEnvio = CampoPrivado<string?>(cut, "_errorAdjuntos");
        avisoDelEnvio.Should().NotBeNullOrEmpty("control positivo: más archivos que el máximo se rechazan con un aviso");

        var envioEnVuelo = Enviar(cut);
        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().ContainSingle("control positivo: el envío está en vuelo");
        if (cambiaElAviso)
        {
            await AdjuntarVariosAsync(cut, [new ArchivoQueNoCabe()]);
            CampoPrivado<string?>(cut, "_errorAdjuntos").Should().NotBe(avisoDelEnvio, "control positivo: salió otro aviso durante el envío");
        }

        await cut.InvokeAsync(() => envio.SetResult(Result.Exito()));
        await envioEnVuelo;
        cut.Render();

        var aviso = CampoPrivado<string?>(cut, "_errorAdjuntos");
        if (cambiaElAviso) aviso.Should().NotBeNullOrEmpty().And.NotBe(avisoDelEnvio, "el aviso nuevo no era el del borrador enviado");
        else aviso.Should().BeNull("el aviso del borrador enviado se retira con él");
    }

    // --- Vista desde la URL (SincronizarFiltrosDesdeUrl, BAJO de la r6) ---

    /// <summary>
    /// BAJO de la r6 de la #2167: cambiar de vista desde la URL (atrás/adelante, enlace compartido) abandona la conversación abierta
    /// igual que el botón. La plantilla en vuelo no acaba en ningún borrador y la conversación abandonada deja de estar abierta.
    /// Detecta quitar <c>DescartarConversacionAbierta()</c> de <c>SincronizarFiltrosDesdeUrl</c>.
    /// </summary>
    [Fact]
    public async Task Cambiar_de_vista_desde_la_URL_con_la_plantilla_en_vuelo_no_la_deja_en_ningun_borrador()
    {
        var macro = PlantillaCertificado();
        var (escenario, _, _) = DosConversacionesConPlantilla(macro);
        var plantilla = Retenida();
        escenario.Interceptar = p => p is AplicarMacroCommand ? plantilla.Task : null;
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera conversación");

        var aplicacion = cut.Find(".composer-macro-campo select").ChangeAsync(new ChangeEventArgs { Value = macro.Id.ToString() });
        cut.WaitForAssertion(() => mediador.Enviados.OfType<AplicarMacroCommand>().Should().ContainSingle("la plantilla está en vuelo"));
        await cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo("comunicaciones?vista=personal"));
        cut.WaitForAssertion(() => CampoPrivado<string>(cut, "_vista").Should().Be("personal", "control positivo: la URL cambió la vista"));
        CampoPrivado<Guid?>(cut, "_conversacionSeleccionadaId").Should().BeNull("la conversación abandonada ya no está abierta");

        await cut.InvokeAsync(() => plantilla.SetResult(Result.Exito("<p>Plantilla de la vista anterior.</p>")));
        await aplicacion;
        cut.Render();

        CampoPrivado<string>(cut, "_textoRespuesta").Should().BeEmpty("la plantilla se pidió para la conversación abandonada");
    }

    private static async Task AbrirDescartandoAsync(IRenderedComponent<Bandeja> cut, string asunto)
    {
        var seleccion = SeleccionarFila(cut, asunto);
        cut.WaitForAssertion(() => cut.FindAll(".modal-pie button").Should().Contain(b => b.TextContent.Trim() == "Salir y descartar"));
        await cut.PulsarEnElAvisoAsync("Salir y descartar");
        await seleccion;
        cut.WaitForAssertion(() => TituloAbierto(cut).Should().Be(asunto));
    }

    /// <summary>Archivo del navegador cuya lectura no avanza hasta que se abre la puerta.</summary>
    private sealed class ArchivoRetenido(string nombre, byte[] contenido, Task puerta) : IBrowserFile
    {
        public bool Abierto { get; private set; }
        public string Name => nombre;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => contenido.Length;
        public string ContentType => "application/pdf";

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            Abierto = true;
            return new FlujoRetenido(new MemoryStream(contenido), puerta);
        }
    }

    private sealed class FlujoRetenido(MemoryStream contenido, Task puerta) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => contenido.Length;
        public override long Position { get => contenido.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => contenido.Read(buffer, offset, count);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await puerta;
            return await contenido.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) contenido.Dispose();
            base.Dispose(disposing);
        }
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

    // ---------------------------------------------------------------- estados de espera (hueco 2 de Comunicaciones)

    [Fact]
    public void El_filtro_de_estado_ofrece_los_seis_estados_con_su_texto_de_negocio()
    {
        var (cut, _) = Renderizar(new Escenario());

        var opciones = SelectorDeEstado(cut).QuerySelectorAll("option").ToList();

        opciones.Select(o => o.GetAttribute("value")).Should().Equal(
            "", "Abierta", "Pendiente", "EsperandoRespuestaExterna", "EnEsperaDeTercero", "Resuelta", "Cerrada");
        opciones.Select(o => o.TextContent.Trim()).Should().Equal(
            "Todos los estados", "Abierta", "Pendiente", "Esperando respuesta del Interlocutor", "En espera de tercero", "Resuelta", "Cerrada");
    }

    [Fact]
    public async Task Filtrar_por_un_estado_de_espera_manda_ese_estado_al_servidor()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.Add(Conversacion("Esperamos a la mutua", ClienteRefrielectric, estado: EstadoConversacion.EnEsperaDeTercero));
        var (cut, mediador) = Renderizar(escenario);

        await SelectorDeEstado(cut).ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoConversacion.EnEsperaDeTercero) });

        mediador.Enviados.OfType<ObtenerConversacionesQuery>().Last().Estado.Should().Be(EstadoConversacion.EnEsperaDeTercero);
        AsuntosVisibles(cut).Should().Equal(["Esperamos a la mutua"]);
    }

    [Theory]
    [InlineData(EstadoConversacion.EsperandoRespuestaExterna, "Esperando respuesta del Interlocutor")]
    [InlineData(EstadoConversacion.EnEsperaDeTercero, "En espera de tercero")]
    public async Task El_chip_de_la_fila_y_el_de_la_cabecera_dicen_el_estado_en_castellano_y_con_tono_de_advertencia(
        EstadoConversacion estado, string texto)
    {
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric, estado: estado);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var (cut, _) = Renderizar(escenario);

        // E13: en la tabla el estado es texto plano (sin pastilla), pero sigue en castellano.
        var estadoFila = cut.Find(".tabla-conversaciones .tabla-estado");
        estadoFila.TextContent.Trim().Should().Be(texto, "el nombre del enum («EsperandoRespuestaExterna») no se enseña al Gestor CAE");
        cut.FindAll(".tabla-conversaciones [data-pieza='pastilla']").Should().BeEmpty();

        await SeleccionarFila(cut, "Documentación pendiente");
        var chipCabecera = cut.Find(".bandeja-centro-meta [data-pieza='pastilla']");
        chipCabecera.TextContent.Trim().Should().Be(texto);
        chipCabecera.ClassName.Should().Contain("advertencia");
    }

    [Fact]
    public async Task El_selector_de_la_cabecera_solo_ofrece_las_transiciones_que_el_dominio_permite()
    {
        var resuelta = Conversacion("Documentación pendiente", ClienteRefrielectric, estado: EstadoConversacion.Resuelta);
        var escenario = new Escenario { Detalle = _ => DetalleDe(resuelta) };
        escenario.Conversaciones.Add(resuelta);
        var (cut, _) = Renderizar(escenario);

        await SeleccionarFila(cut, "Documentación pendiente");

        SelectorCambiarEstado(cut).QuerySelectorAll("option:not([disabled])").Select(o => o.TextContent.Trim()).Should().Equal("Abierta");
        SelectorCambiarEstado(cut).GetAttribute("value").Should().Be(nameof(EstadoConversacion.Resuelta));
    }

    [Fact]
    public async Task Elegir_Esperando_respuesta_externa_en_el_selector_manda_el_cambio_de_estado()
    {
        var abierta = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(abierta) };
        escenario.Conversaciones.Add(abierta);
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");

        SelectorCambiarEstado(cut).QuerySelectorAll("option").Select(o => o.TextContent.Trim()).Should().Equal(
            "Abierta", "Esperando respuesta del Interlocutor", "En espera de tercero", "Resuelta", "Cerrada");
        SelectorCambiarEstado(cut).QuerySelector("option[value=Cerrada]")!.HasAttribute("disabled").Should().BeTrue();
        await SelectorCambiarEstado(cut).ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoConversacion.EsperandoRespuestaExterna) });

        mediador.Enviados.OfType<CambiarEstadoConversacionCommand>().Should().ContainSingle()
            .Which.Should().Be(new CambiarEstadoConversacionCommand(abierta.Id, EstadoConversacion.EsperandoRespuestaExterna));
    }

    [Fact]
    public async Task Una_transicion_rechazada_por_el_dominio_se_avisa_y_no_recarga_nada()
    {
        var abierta = Conversacion("Documentación pendiente", ClienteRefrielectric);
        var escenario = new Escenario
        {
            Detalle = _ => DetalleDe(abierta),
            Interceptar = peticion => peticion is CambiarEstadoConversacionCommand
                ? Task.FromResult<object?>(Result.Fallo(Error.Crear("Conversacion.TransicionDeEstadoNoPermitida", "Una conversación Cerrada no puede pasar a Resuelta.")))
                : null
        };
        escenario.Conversaciones.Add(abierta);
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Documentación pendiente");

        await SelectorCambiarEstado(cut).ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoConversacion.Resuelta) });

        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle()
            .Which.Mensaje.Should().Be("Una conversación Cerrada no puede pasar a Resuelta.");
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

        var seleccion = SelectorDeCliente(cut);
        var filtroRefrielectric = seleccion.ChangeAsync(new ChangeEventArgs { Value = ClienteRefrielectricId.ToString() });
        var filtroEbro = SelectorDeCliente(cut)
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
    /// Empresarial es el <b>Cliente empresarial</b>. Fuera de la bandeja la pantalla
    /// lo rotula «Cliente» (decisión de rótulo del 2026-10-09); en la bandeja de
    /// Comunicaciones, «Cliente empresarial» (decisión E24 del 2026-11-11): con
    /// mayúscula y artículo, nunca las formas sueltas anteriores ni «Cliente» a secas. El rótulo de espera, en cambio, nombra al
    /// Interlocutor (decisión de Chris del 2026-11-11, hallazgo MEDIO 3 de la r1 de la #2167): vale para Inbound y Outbound.
    /// </summary>
    [Fact]
    public void Los_rotulos_de_la_bandeja_nombran_al_Cliente_empresarial_con_su_rotulo_de_pantalla()
    {
        var (cut, _) = Renderizar(new Escenario());

        cut.Markup.Should()
            .Contain("Esperando respuesta del Interlocutor")
            .And.Contain("Todos los Clientes empresariales")
            .And.Contain("Los mensajes nuevos aparecerán aquí.");

        cut.Markup.Should()
            .NotContain("Esperando cliente")
            .And.NotContain("Todos los clientes")
            .And.NotContain("Sin cliente asignado")
            .And.NotContain("Esperando al Cliente<")
            .And.NotContain("Todos los Clientes<");
    }

    // ---------------------------------------------------------------- cambios sin guardar (P1-E2b)

    private static readonly Guid CentroNorteId = Guid.NewGuid();
    private static readonly Guid AdjuntoId = Guid.NewGuid();

    /// <summary>
    /// Una conversación de Refrielectric con un adjunto, un Centro para «Pedir prioridad» y un
    /// buzón para «Redactar»: lo mínimo para abrir los cinco formularios de la página.
    /// </summary>
    private async Task<(IRenderedComponent<Bandeja> Cut, NavigationManager Navegacion)> RenderizarConversacionAbiertaAsync(
        bool laDeteccionLeeLaFechaDeEmision = true, bool conBuzon = true, bool borradorFalla = false, bool envioFalla = false)
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
                ObtenerBorradorPedirPrioridadQuery when borradorFalla => Task.FromResult<object?>(
                    Result.Fallo<BorradorPedirPrioridadDto>(Error.Crear("Prioridad.SinBorrador", "No pudimos preparar el borrador."))),
                PedirPrioridadValidacionCommand => Task.FromResult<object?>(Result.Exito()),
                ObtenerBorradorPedirPrioridadQuery => Task.FromResult<object?>(Result.Exito(new BorradorPedirPrioridadDto(
                    "validacion@example.invalid", "Prioridad Centro Norte", "<p>Rogamos prioridad.</p>", conBuzon, null, 2))),
                ObtenerConexionesIntegracionQuery when !conBuzon => Task.FromResult<object?>(new List<ConexionIntegracionListaDto>()),
                ObtenerConexionesIntegracionQuery => Task.FromResult<object?>(new List<ConexionIntegracionListaDto>
                    { new(Guid.NewGuid(), "cae@example.invalid", "CAE Norte", null, null, EstadoConexionIntegracion.Habilitada, DateTime.UtcNow, null, null) }),
                EnviarMensajeNuevoCommand when envioFalla => Task.FromResult<object?>(
                    Result.Fallo<Guid>(Error.Crear("Mensaje.NoEnviado", "El buzón rechazó el envío."))),
                EnviarMensajeNuevoCommand => Task.FromResult<object?>(Result.Exito(Guid.NewGuid())),
                ObtenerEmpresasParaSelectorQuery => Task.FromResult<object?>(new List<EmpresaSelectorDto>()),
                CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto.ActualizarDocumentoDesdeAdjuntoCommand =>
                    Task.FromResult<object?>(Result.Exito(Guid.NewGuid())),
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
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Nuevo correo").ClickAsync(new MouseEventArgs());

        cut.FindAll(".drawer-panel").Should().ContainSingle("el test necesita el drawer de redactar abierto");
        await cut.Find(".drawer-panel button.drawer-cerrar").ClickAsync(new MouseEventArgs());
        cut.FindAll(".drawer-panel").Should().BeEmpty("sin cambios, la X cierra sin preguntar");

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Nuevo correo").ClickAsync(new MouseEventArgs());
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

    /// <summary>
    /// Un vencimiento a medias o ilegible llega al formulario como «»: aplicar así actualizaría el documento «sin vencimiento».
    /// La pantalla consume la bandera del campo y no envía el comando mientras sea true.
    /// </summary>
    [Fact]
    public async Task Un_vencimiento_a_medias_no_actualiza_el_documento_sin_vencimiento()
    {
        var (cut, _) = await RenderizarConversacionAbiertaAsync();
        await cut.Find(".timeline-adjunto-actualizar-documento").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".modal-actualizar-documento-formulario").Should().ContainSingle());
        await cut.InvokeAsync(() =>
        {
            var tipo = typeof(Bandeja).GetField("_tipoDocumentoIdFormulario", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var trabajador = typeof(Bandeja).GetField("_trabajadorDocumentoIdFormulario", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            tipo.SetValue(cut.Instance, Guid.NewGuid().ToString());
            trabajador.SetValue(cut.Instance, Guid.NewGuid().ToString());
        });
        cut.Render();
        var mediador = (MediadorControlado)Services.GetRequiredService<IMediator>();
        var vencimiento = () => cut.FindAll(".modal-actualizar-documento-formulario input[data-campo-fecha]")[1];
        var aplicar = () => cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Aplicar y registrar");

        await vencimiento().InputAsync(new ChangeEventArgs { Value = "15/03" });
        await aplicar().ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto.ActualizarDocumentoDesdeAdjuntoCommand>()
            .Should().BeEmpty("el texto a medias llega como «» y se aplicaría como «sin vencimiento»");
        cut.Markup.Should().Contain("no es válida");

        await vencimiento().InputAsync(new ChangeEventArgs { Value = "15/03/2030" });
        await aplicar().ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto.ActualizarDocumentoDesdeAdjuntoCommand>()
            .Should().ContainSingle("control positivo: con la fecha entera sí se aplica")
            .Which.FechaVencimientoManual.Should().Be(new DateOnly(2030, 3, 15));
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

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Nuevo correo").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find(".drawer-panel .aviso-sin-buzon").TextContent.Should().Contain("no hay ningún buzón de Microsoft 365 conectado"));
        cut.Find(".drawer-panel .aviso-sin-buzon-pedir").TextContent.Should().Contain("rol Administrador de esta organización");
        var enviar = cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Enviar");
        enviar.HasAttribute("disabled").Should().BeTrue("sin buzón no hay nada que enviar, y el kit exige decir por qué");
        enviar.GetAttribute("title").Should().Contain("no hay ningún buzón de Microsoft 365 conectado");
        cut.FindAll(".drawer-panel input.campo-input").Should().BeEmpty();
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty();
    }

    [Fact]
    public async Task Pedir_prioridad_sin_buzon_conectado_deja_copiar_el_borrador_y_dice_a_quien_pedir_el_buzon()
    {
        var (cut, _) = await RenderizarConversacionAbiertaAsync(conBuzon: false);
        await cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });

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
        await cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Pedir prioridad de validación").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "Prioridad Centro Norte"));

        await cut.SalirYComprobarQueNoPreguntaAsync(navegacion, "el borrador propuesto no es un cambio de quien lo revisa");
    }

    [Fact]
    public async Task Editar_el_borrador_de_pedir_prioridad_y_salir_pregunta()
    {
        var (cut, navegacion) = await RenderizarConversacionAbiertaAsync();
        await cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
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
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Nuevo correo").ClickAsync(new MouseEventArgs());
        await PulsarCancelarEnAsync(cut, ".drawer-pie");
        cut.FindAll(".drawer-panel").Should().BeEmpty("sin cambios, Cancelar cierra directamente");
        PreguntaDescartar(cut).Should().BeFalse();

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Nuevo correo").ClickAsync(new MouseEventArgs());
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
        await cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
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

    private MediadorControlado MediadorDeLaPagina => (MediadorControlado)Services.GetRequiredService<IMediator>();

    [Fact]
    public async Task Enviar_Redactar_manda_el_comando_con_lo_escrito_y_cierra()
    {
        var (cut, _) = await RenderizarConversacionAbiertaAsync();
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Nuevo correo").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".drawer-panel input.campo-input")[0].InputAsync(new ChangeEventArgs { Value = "contacto@example.invalid" });
        await cut.FindAll(".drawer-panel input.campo-input")[1].InputAsync(new ChangeEventArgs { Value = "Asunto nuevo" });
        await cut.Find(".drawer-panel textarea").InputAsync(new ChangeEventArgs { Value = "Cuerpo nuevo." });

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Enviar").ClickAsync(new MouseEventArgs());

        var comando = MediadorDeLaPagina.Enviados.OfType<EnviarMensajeNuevoCommand>().Should().ContainSingle().Subject;
        comando.Destinatarios.Should().Equal("contacto@example.invalid");
        comando.Asunto.Should().Be("Asunto nuevo");
        comando.CuerpoHtml.Should().Be("Cuerpo nuevo.");
        cut.FindAll(".drawer-panel").Should().BeEmpty();
    }

    [Fact]
    public async Task Enviar_Redactar_sin_destinatario_dice_por_que_y_no_cierra_ni_envia()
    {
        var (cut, _) = await RenderizarConversacionAbiertaAsync();
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Nuevo correo").ClickAsync(new MouseEventArgs());

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Enviar").ClickAsync(new MouseEventArgs());

        cut.Find(".drawer-aviso").TextContent.Should().Contain("Indica al menos un destinatario.", "el error sale en el aviso del kit, fuera del cuerpo desplazable");
        cut.FindAll(".drawer-panel").Should().ContainSingle();
        MediadorDeLaPagina.Enviados.OfType<EnviarMensajeNuevoCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Si_el_envio_de_Redactar_falla_el_Drawer_sigue_abierto_con_lo_escrito_y_el_motivo()
    {
        var (cut, _) = await RenderizarConversacionAbiertaAsync(envioFalla: true);
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Nuevo correo").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".drawer-panel input.campo-input")[0].InputAsync(new ChangeEventArgs { Value = "contacto@example.invalid" });

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Enviar").ClickAsync(new MouseEventArgs());

        cut.Find(".drawer-aviso").TextContent.Should().Contain("El buzón rechazó el envío.");
        cut.FindAll(".drawer-panel").Should().ContainSingle();
        cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "contacto@example.invalid", "lo escrito se conserva para corregir");
        cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Enviar").HasAttribute("disabled").Should().BeFalse("se puede corregir y reintentar");
    }

    [Fact]
    public async Task Enviar_Pedir_prioridad_manda_el_comando_con_el_borrador_editado_y_cierra()
    {
        var (cut, _) = await RenderizarConversacionAbiertaAsync();
        await cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Pedir prioridad de validación").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "Prioridad Centro Norte"));
        await cut.FindAll(".drawer-panel textarea").Single().InputAsync(new ChangeEventArgs { Value = "Otro texto." });

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Enviar").ClickAsync(new MouseEventArgs());

        var comando = MediadorDeLaPagina.Enviados.OfType<PedirPrioridadValidacionCommand>().Should().ContainSingle().Subject;
        comando.CentroId.Should().Be(CentroNorteId);
        comando.Destinatarios.Should().Equal("validacion@example.invalid");
        comando.Asunto.Should().Be("Prioridad Centro Norte");
        comando.CuerpoHtml.Should().Be("Otro texto.");
        cut.FindAll(".drawer-panel").Should().BeEmpty();
    }

    [Fact]
    public async Task Enviar_Pedir_prioridad_sin_destinatario_dice_por_que_y_no_envia()
    {
        var (cut, _) = await RenderizarConversacionAbiertaAsync();
        await cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Pedir prioridad de validación").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "Prioridad Centro Norte"));
        await cut.FindAll(".drawer-panel input.campo-input")[0].InputAsync(new ChangeEventArgs { Value = " " });

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Enviar").ClickAsync(new MouseEventArgs());

        cut.Find(".drawer-aviso").TextContent.Should().Contain("Indica al menos un destinatario.");
        cut.FindAll(".drawer-panel").Should().ContainSingle();
        MediadorDeLaPagina.Enviados.OfType<PedirPrioridadValidacionCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Pedir_prioridad_sin_borrador_pinta_el_error_y_deja_Enviar_deshabilitado_con_su_motivo()
    {
        var (cut, _) = await RenderizarConversacionAbiertaAsync(borradorFalla: true);
        await cut.Find(".composer-correo-prellenados select").ChangeAsync(new ChangeEventArgs { Value = CentroNorteId.ToString() });

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Pedir prioridad de validación").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find(".drawer-panel").TextContent.Should().Contain("No pudimos preparar el borrador."));
        var enviar = cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Enviar");
        enviar.HasAttribute("disabled").Should().BeTrue();
        enviar.GetAttribute("title").Should().Be("No pudimos preparar el borrador.");
        cut.FindAll(".drawer-panel input.campo-input").Should().BeEmpty();
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

        var cambio = cut.FindAll(".bandeja-nav-item").Single(b => b.TextContent.Contains("personal", StringComparison.OrdinalIgnoreCase))
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
        // Los formularios en DrawerFormulario montan su propio NavigationLock (cerrados, sin armar): se mira el conjunto.
        cut.FindComponents<NavigationLock>().Should().OnlyContain(l => !l.Instance.ConfirmExternalNavigation);

        await cut.Find(".composer-nota-interna textarea").InputAsync(new ChangeEventArgs { Value = "Llamar antes de las 10." });

        cut.FindComponents<NavigationLock>().Should().Contain(l => l.Instance.ConfirmExternalNavigation,
            "recargar o cerrar la pestaña con la nota a medias tiene que avisar");
    }

    // ---------------------------------------------------------------- macro, Ctrl+Intro, j/k y errores accionables

    [Fact]
    public async Task Resolver_avanza_a_la_siguiente_del_filtro_actual()
    {
        var escenario = new Escenario();
        var primera = Conversacion("Primera", escenario.Clientes[0]);
        var segunda = Conversacion("Segunda", escenario.Clientes[0]);
        var excluida = Conversacion("Fuera del filtro", escenario.Clientes[1]);
        escenario.Conversaciones.AddRange([primera, segunda, excluida]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        escenario.Interceptar = peticion =>
        {
            if (peticion is not CambiarEstadoConversacionCommand cambio) return null;
            var indice = escenario.Conversaciones.FindIndex(c => c.Id == cambio.ConversacionId);
            escenario.Conversaciones[indice] = escenario.Conversaciones[indice] with { Estado = cambio.NuevoEstado };
            return Task.FromResult<object?>(Result.Exito());
        };
        var (cut, mediador) = Renderizar(escenario, $"comunicaciones?estado=Abierta&cliente={primera.ClienteId}");
        await SeleccionarFila(cut, "Primera");

        await SelectorCambiarEstado(cut).ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoConversacion.Resuelta) });

        cut.Find(".bandeja-centro-titulo h2").TextContent.Should().Be("Segunda");
        mediador.Enviados.OfType<ObtenerConversacionesQuery>().Should().OnlyContain(q =>
            q.Estado == EstadoConversacion.Abierta && q.ClienteId == primera.ClienteId);
        mediador.Enviados.OfType<ObtenerConversacionPorIdQuery>().Should().NotContain(q => q.Id == excluida.Id);
    }

    [Fact]
    public async Task Siguiente_se_deshabilita_al_final_y_no_cambia_el_estado()
    {
        var escenario = new Escenario();
        var primera = Conversacion("Primera", escenario.Clientes[0]);
        var segunda = Conversacion("Segunda", escenario.Clientes[0]);
        escenario.Conversaciones.AddRange([primera, segunda]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera");

        await Boton(cut, "button", "Siguiente conversación").ClickAsync(new MouseEventArgs());

        cut.Find(".bandeja-centro-titulo h2").TextContent.Should().Be("Segunda");
        Boton(cut, "button", "Siguiente conversación").HasAttribute("disabled").Should().BeTrue();
        mediador.Enviados.OfType<CambiarEstadoConversacionCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Un_fallo_al_resolver_conserva_el_hilo()
    {
        var escenario = new Escenario();
        var primera = Conversacion("Primera", escenario.Clientes[0]);
        escenario.Conversaciones.AddRange([primera, Conversacion("Segunda", escenario.Clientes[0])]);
        escenario.Detalle = _ => DetalleDe(primera);
        escenario.Interceptar = p => p is CambiarEstadoConversacionCommand
            ? Task.FromResult<object?>(Result.Fallo(Error.Crear("Prueba.Fallo", "No se pudo resolver."))) : null;
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera");
        await SelectorCambiarEstado(cut).ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoConversacion.Resuelta) });
        cut.Find(".bandeja-centro-titulo h2").TextContent.Should().Be("Primera");
    }

    [Fact]
    public async Task Siguiente_cruza_la_pagina_con_el_mismo_filtro()
    {
        var escenario = new Escenario();
        for (var i = 1; i <= 21; i++) escenario.Conversaciones.Add(Conversacion($"Hilo {i}", escenario.Clientes[0]));
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        var (cut, mediador) = Renderizar(escenario, $"comunicaciones?cliente={escenario.Clientes[0].Id}");
        await SeleccionarFila(cut, "Hilo 20");
        await Boton(cut, "button", "Siguiente conversación").ClickAsync(new MouseEventArgs());
        cut.Find(".bandeja-centro-titulo h2").TextContent.Should().Be("Hilo 21");
        var consulta = mediador.Enviados.OfType<ObtenerConversacionesQuery>().Last();
        consulta.Pagina.Should().Be(2);
        consulta.ClienteId.Should().Be(escenario.Clientes[0].Id);
    }

    [Theory]
    [InlineData(CanalConversacion.Correo, "<p>Falta el certificado.</p>")]
    [InlineData(CanalConversacion.WhatsApp, "Falta el certificado.")]
    public async Task Aplicar_macro_completa_el_borrador_sin_enviar(CanalConversacion canal, string esperado)
    {
        var escenario = new Escenario();
        var conversacion = Conversacion("Solicitud", escenario.Clientes[0]) with { Canal = canal };
        escenario.Conversaciones.Add(conversacion);
        escenario.Detalle = _ => DetalleDe(conversacion) with { Canal = canal, FechaUltimoMensajeEntranteUtc = DateTime.UtcNow };
        var macro = new MacroListaDto(Guid.NewGuid(), null, null, "Certificado", "<p>Falta el certificado.</p>", Guid.NewGuid());
        escenario.Macros.Add(macro);
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Solicitud");
        await cut.Find(".composer-macro-campo select").ChangeAsync(new ChangeEventArgs { Value = macro.Id.ToString() });
        cut.FindComponent<ComposerBar>().Instance.Texto.Should().Be(esperado);
        mediador.Enviados.OfType<AplicarMacroCommand>().Should().ContainSingle(c => c.ConversacionId == conversacion.Id && c.MacroId == macro.Id);
        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Resolver_con_respuesta_tardia_no_cambia_el_hilo_que_ya_se_esta_viendo()
    {
        var escenario = new Escenario();
        var primera = Conversacion("Primera", escenario.Clientes[0]);
        var segunda = Conversacion("Segunda", escenario.Clientes[0]);
        escenario.Conversaciones.AddRange([primera, segunda, Conversacion("Tercera", escenario.Clientes[0])]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        var respuesta = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p is CambiarEstadoConversacionCommand ? respuesta.Task : null;
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera");
        var cambio = SelectorCambiarEstado(cut).ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoConversacion.Resuelta) });
        await SeleccionarFila(cut, "Segunda");
        respuesta.SetResult(Result.Exito());
        await cambio;
        cut.Find(".bandeja-centro-titulo h2").TextContent.Should().Be("Segunda");
    }

    /// <summary>
    /// El aviso de tiempo real que llega de un job de fondo: pasa por la misma ruta privada que en producción
    /// (<c>AlRecibirMensajeAsync</c>) y recarga la lista, con lo que avanza el contador de cargas vigentes.
    /// El id de conversación es ajeno a la selección, así que solo refresca la lista.
    /// </summary>
    private static Task RefrescoDeFondo(IRenderedComponent<Bandeja> cut) =>
        (Task)typeof(Bandeja).GetMethod("AlRecibirMensajeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cut.Instance, [new MensajeWhatsAppRecibidoEvent(Guid.NewGuid(), Guid.NewGuid())])!;

    private static string TituloAbierto(IRenderedComponent<Bandeja> cut) =>
        cut.Find(".bandeja-centro-titulo h2").TextContent;

    [Fact]
    public async Task Resolver_con_un_refresco_de_fondo_en_vuelo_sigue_recargando_el_detalle_y_avanza()
    {
        var escenario = new Escenario();
        var primera = Conversacion("Primera", escenario.Clientes[0]);
        escenario.Conversaciones.AddRange([primera, Conversacion("Segunda", escenario.Clientes[0])]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        var respuesta = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p is CambiarEstadoConversacionCommand ? respuesta.Task : null;
        var (cut, mediador) = Renderizar(escenario, "comunicaciones?estado=Abierta");
        await SeleccionarFila(cut, "Primera");
        var detallesAntes = mediador.Enviados.OfType<ObtenerConversacionPorIdQuery>().Count(q => q.Id == primera.Id);

        var cambio = SelectorCambiarEstado(cut).ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoConversacion.Resuelta) });
        await RefrescoDeFondo(cut);
        escenario.Conversaciones[0] = primera with { Estado = EstadoConversacion.Resuelta };
        respuesta.SetResult(Result.Exito());
        await cambio;

        TituloAbierto(cut).Should().Be("Segunda");
        mediador.Enviados.OfType<ObtenerConversacionPorIdQuery>().Count(q => q.Id == primera.Id)
            .Should().BeGreaterThan(detallesAntes, "el detalle de la conversación resuelta se recarga aunque la lista se refrescara en medio");
    }

    [Fact]
    public async Task Resolver_elige_la_sucesora_por_identidad_aunque_la_lista_se_reordene()
    {
        var escenario = new Escenario();
        var primera = Conversacion("Primera", escenario.Clientes[0]);
        escenario.Conversaciones.AddRange([primera, Conversacion("Segunda", escenario.Clientes[0]), Conversacion("Tercera", escenario.Clientes[0])]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        escenario.Interceptar = p =>
        {
            if (p is not CambiarEstadoConversacionCommand) return null;
            // Mientras se resuelve, entra otra conversación por arriba: por índice se abriría la recién llegada.
            escenario.Conversaciones[0] = primera with { Estado = EstadoConversacion.Resuelta };
            escenario.Conversaciones.Insert(0, Conversacion("Recién llegada", escenario.Clientes[0]));
            return Task.FromResult<object?>(Result.Exito());
        };
        var (cut, mediador) = Renderizar(escenario, "comunicaciones?estado=Abierta");
        await SeleccionarFila(cut, "Primera");

        await SelectorCambiarEstado(cut).ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoConversacion.Resuelta) });

        TituloAbierto(cut).Should().Be("Segunda");
        AsuntosVisibles(cut).Should().Equal("Recién llegada", "Segunda", "Tercera");
    }

    [Fact]
    public async Task Resolver_con_la_fila_aun_en_el_filtro_avanza_igualmente_a_la_siguiente_fila()
    {
        var escenario = new Escenario();
        var primera = Conversacion("Primera", escenario.Clientes[0]);
        escenario.Conversaciones.AddRange([primera, Conversacion("Segunda", escenario.Clientes[0])]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        escenario.Interceptar = p =>
        {
            if (p is not CambiarEstadoConversacionCommand) return null;
            escenario.Conversaciones[0] = primera with { Estado = EstadoConversacion.Resuelta };
            return Task.FromResult<object?>(Result.Exito());
        };
        var (cut, _) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera");

        await SelectorCambiarEstado(cut).ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoConversacion.Resuelta) });

        TituloAbierto(cut).Should().Be("Segunda");
        AsuntosVisibles(cut).Should().Equal("Primera", "Segunda");
    }

    [Fact]
    public async Task Resolver_la_ultima_fila_sin_sucesora_conserva_el_hilo_y_no_abre_otro()
    {
        var escenario = new Escenario();
        var anterior = Conversacion("Anterior", escenario.Clientes[0]);
        var ultima = Conversacion("Última", escenario.Clientes[0]);
        escenario.Conversaciones.AddRange([anterior, ultima]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        escenario.Interceptar = p =>
        {
            if (p is not CambiarEstadoConversacionCommand) return null;
            escenario.Conversaciones[1] = ultima with { Estado = EstadoConversacion.Resuelta };
            return Task.FromResult<object?>(Result.Exito());
        };
        var (cut, mediador) = Renderizar(escenario, "comunicaciones?estado=Abierta");
        await SeleccionarFila(cut, "Última");

        await SelectorCambiarEstado(cut).ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoConversacion.Resuelta) });

        TituloAbierto(cut).Should().Be("Última");
        mediador.Enviados.OfType<ObtenerConversacionPorIdQuery>().Should().NotContain(q => q.Id == anterior.Id);
        AsuntosVisibles(cut).Should().Equal("Anterior");
    }

    [Fact]
    public async Task Siguiente_tras_cruzar_la_pagina_aguanta_un_refresco_de_fondo_en_medio()
    {
        var escenario = new Escenario();
        for (var i = 1; i <= 21; i++) escenario.Conversaciones.Add(Conversacion($"Hilo {i}", escenario.Clientes[0]));
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        var paginaDos = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObtenerConversacionesQuery? pedida = null;
        escenario.Interceptar = p =>
        {
            if (p is not ObtenerConversacionesQuery { Pagina: 2 } q || pedida is not null) return null;
            pedida = q;
            return paginaDos.Task;
        };
        var (cut, _) = Renderizar(escenario, $"comunicaciones?cliente={escenario.Clientes[0].Id}");
        await SeleccionarFila(cut, "Hilo 20");

        var siguiente = Boton(cut, "button", "Siguiente conversación").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => pedida.Should().NotBeNull());
        await RefrescoDeFondo(cut);
        paginaDos.SetResult(escenario.Pagina(pedida!));
        await siguiente;

        // El clic devuelve al primer punto de espera del manejador; el destino se alcanza tras soltar la carga.
        cut.WaitForAssertion(() => TituloAbierto(cut).Should().Be("Hilo 21"));
    }

    [Fact]
    public async Task Siguiente_tambien_lo_tiene_el_rol_de_consulta_porque_navegar_no_escribe()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var escenario = new Escenario();
        escenario.Conversaciones.AddRange([Conversacion("Primera", escenario.Clientes[0]), Conversacion("Segunda", escenario.Clientes[0])]);
        escenario.Detalle = id => DetalleDe(escenario.Conversaciones.Single(c => c.Id == id));
        var (cut, mediador) = Renderizar(escenario);
        await SeleccionarFila(cut, "Primera");

        cut.FindAll(".composer").Should().BeEmpty("Consulta no ve el editor, que escribe");
        await Boton(cut, "button", "Siguiente conversación").ClickAsync(new MouseEventArgs());

        TituloAbierto(cut).Should().Be("Segunda");
        mediador.Enviados.OfType<CambiarEstadoConversacionCommand>().Should().BeEmpty();
    }

    private static IElement TextareaCorreo(IRenderedComponent<Bandeja> cut) => cut.Find(".composer-correo textarea");

    private static Task Teclear(IElement elemento, string tecla, bool ctrl = false, bool meta = false) =>
        elemento.KeyDownAsync(new KeyboardEventArgs { Key = tecla, CtrlKey = ctrl, MetaKey = meta });

    [Fact]
    public async Task El_nucleo_recupera_macros_sin_perder_la_respuesta()
    {
        var conversacion = Conversacion("Documentos", ClienteRefrielectric) with { SlaRestante = TimeSpan.FromHours(2) };
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var (cut, _) = Renderizar(escenario);
        cut.FindAll(".tabla-conversaciones [role=columnheader]").Select(e => e.TextContent.Trim())
            .Should().Equal("Prioridad", "Conversaci\u00f3n", "Cliente empresarial", "Estado", "Asignada a");
        await cut.Find(".tabla-conversaciones-asunto button").ClickAsync(new MouseEventArgs());
        await Escribir(cut, "Recibido.");
        cut.FindAll(".composer-macro-campo").Should().ContainSingle();
        cut.FindAll(".bandeja-fila-linea3 [title]").Should().NotContain(e => e.GetAttribute("title")!.Contains("SLA"));
        cut.FindComponent<ComposerBar>().Instance.Texto.Should().Be("Recibido.");
    }

    [Fact]
    public async Task Volver_a_la_bandeja_cierra_el_hilo_y_quita_solo_la_conversacion_de_la_URL()
    {
        var conversacion = Conversacion("Documentos", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var (cut, mediador) = Renderizar(escenario, "comunicaciones?busqueda=Documentos");
        await cut.Find(".tabla-conversaciones-asunto button").ClickAsync(new MouseEventArgs());
        cut.FindAll(".bandeja-modo-hilo").Should().ContainSingle();
        await cut.Find(".bandeja-volver").ClickAsync(new MouseEventArgs());
        cut.FindAll(".bandeja-modo-tabla").Should().ContainSingle();
        cut.FindAll(".tabla-conversaciones-asunto button").Should().ContainSingle();
        var uri = Services.GetRequiredService<NavigationManager>().Uri;
        uri.Should().Contain("busqueda=Documentos").And.NotContain("conversacion=");
        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Alternar_nota_interna_y_respuesta_conserva_cada_borrador_y_no_envia()
    {
        var (cut, _, mediador, _) = await RenderizarDosConversacionesAsync();
        await Escribir(cut, "Respuesta para el contacto.");
        var tabs = cut.FindAll(".bandeja-editor-tabs button");
        tabs[0].GetAttribute("aria-pressed").Should().Be("true");
        cut.Find(".bandeja-editor-nota").HasAttribute("hidden").Should().BeTrue();
        await tabs[1].ClickAsync(new MouseEventArgs());
        cut.Find(".bandeja-editor-publico").HasAttribute("hidden").Should().BeTrue();
        await cut.Find(".composer-nota-interna textarea").InputAsync(new ChangeEventArgs { Value = "Nota del equipo." });
        await cut.FindAll(".bandeja-editor-tabs button")[0].ClickAsync(new MouseEventArgs());
        cut.FindComponent<ComposerBar>().Instance.Texto.Should().Be("Respuesta para el contacto.");
        await cut.FindAll(".bandeja-editor-tabs button")[1].ClickAsync(new MouseEventArgs());
        cut.FindComponents<CampoTextarea>().Should().Contain(c => c.Instance.Valor == "Nota del equipo.");
        mediador.Enviados.OfType<ResponderConversacionCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Volver_con_respuesta_sin_guardar_permite_seguir_editando()
    {
        var (cut, _, _, _) = await RenderizarDosConversacionesAsync();
        await Escribir(cut, "No perder esta respuesta.");
        var volver = cut.Find(".bandeja-volver").ClickAsync(new MouseEventArgs());
        await cut.PulsarEnElAvisoAsync("Seguir editando");
        await volver;
        cut.FindAll(".bandeja-modo-hilo").Should().ContainSingle();
        cut.FindComponent<ComposerBar>().Instance.Texto.Should().Be("No perder esta respuesta.");
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
        cut.FindAll(".bandeja-fila.fila-enfocada .bandeja-fila-asunto, .tabla-conversaciones-fila.fila-enfocada .tabla-conversaciones-asunto button").Select(f => f.TextContent.Trim()).ToList();

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
    public async Task Abrir_por_clic_situa_j_k_en_el_hilo_abierto_sin_agrupar_por_Cliente_empresarial()
    {
        var primera = Conversacion("Primera", ClienteRefrielectric);
        var segunda = Conversacion("Segunda", ClienteEbro);
        var tercera = Conversacion("Tercera", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = id => DetalleDe(id == tercera.Id ? tercera : segunda) };
        escenario.Conversaciones.AddRange([primera, segunda, tercera]);
        var (cut, mediador) = Renderizar(escenario);

        AsuntosVisibles(cut).Should().Equal("Primera", "Segunda", "Tercera");
        await SeleccionarFila(cut, "Segunda");
        AsuntosVisibles(cut).Should().Equal("Primera", "Segunda", "Tercera");
        await PulsarAtajoDeLista(cut, "j");
        AsuntosEnfocados(cut).Should().Equal("Tercera");
        await PulsarAtajoDeLista(cut, "Enter");
        mediador.Enviados.OfType<ObtenerConversacionPorIdQuery>().Last().Id.Should().Be(tercera.Id);
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
