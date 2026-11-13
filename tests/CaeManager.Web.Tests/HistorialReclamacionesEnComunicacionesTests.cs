using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerCompanerosGestorCae;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Comunicaciones.Queries.ObtenerMacros;
using CaeManager.Application.Comunicaciones.Queries.ObtenerMensajesBuzonPersonal;
using CaeManager.Application.Comunicaciones.Queries.ObtenerNotasInternasConversacion;
using CaeManager.Application.Comunicaciones.Queries.ObtenerRecordatorioPropuestoConversacion;
using CaeManager.Application.Comunicaciones.Queries.ObtenerRespuestaSugerida;
using CaeManager.Application.Comunicaciones.Eventos;
using CaeManager.Application.Contactos;
using CaeManager.Application.Contactos.Queries.ObtenerNotaContacto;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresaEmpleadora;
using CaeManager.Application.Reclamaciones.Commands.PrepararVistaPreviaReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionesEnviadas;
using CaeManager.Application.Telemetria.Queries.ObtenerTiempoGestionConversacion;
using CaeManager.Application.Tenants;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Components;
using CaeManager.Web.Features.Comunicaciones.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
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
/// PR6b de la serie «Documentos · listado sin pestañas»: el historial de reclamaciones que mostraba la pestaña Reclamaciones de
/// <c>/documentos</c> (<c>ReclamacionesTab</c>) vive ahora en <c>/comunicaciones</c> como filtro Origen «Reclamaciones».
///
/// <para>
/// <b>Lo que esto SÍ observa</b> (la regla de la retro: lo que tenía main, fijado en su nuevo sitio): cada elemento de la fila
/// (título, detalle, Cliente, estado de respuesta), las acciones por rol («Reclamar de nuevo» exige escritura y que la fila sea
/// reenviable; «Ver hilo» exige conversación), los estados (cargando, error con «Reintentar», vacío por rol), el paginador, la
/// revisión previa al reenvío y su concurrencia; y que el historial lo ven exactamente los roles que lo ven en main
/// (<c>RolesQueLeenReclamaciones</c>): Consulta no lo ve ni se le pregunta nada al servidor.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> la autorización real de los handlers (la query acota por cartera en el servidor, no por rol; eso lo
/// cubren los tests de Application), el contenido de los correos ni el aspecto (CSS, comprobado en la revisión píxel).
/// </para>
/// </summary>
public class HistorialReclamacionesEnComunicacionesTests : BunitContext
{
    public HistorialReclamacionesEnComunicacionesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
        Services.AddLocalization();
    }

    // ---------------------------------------------------------------- dobles

    private sealed class MediadorDePrueba : IMediator
    {
        public List<object> Enviadas { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public Func<ObtenerReclamacionesEnviadasQuery, ResultadoPaginado<ReclamacionEnviadaDto>> Historial { get; set; } =
            q => new ResultadoPaginado<ReclamacionEnviadaDto>([], 0, q.Pagina, q.TamanoPagina);

        public Func<PrepararVistaPreviaReclamacionCommand, Result<ReclamacionPreparada>> Previa { get; set; } =
            q => Result.Exito(new ReclamacionPreparada(
                q.TitularId, "Refrielectric SL", q.DocumentoIds, ["contacto@refrielectric.example"],
                [new DestinatarioAgendaDto(Guid.NewGuid(), "Contacto Refri", "contacto@refrielectric.example", ["RLC"])],
                "Asunto de la vista previa", "<p>Texto de la vista previa</p>"));

        public Func<EnviarReclamacionCommand, Result<EnvioReclamacionResultado>>? AlEnviar { get; set; }

        /// <summary>Conversaciones que devuelve la lista de la bandeja y cuyo detalle se abre.</summary>
        public List<ConversacionListaDto> Conversaciones { get; } = [];

        /// <summary>Si devuelve una tarea, esa consulta se queda esperando a que el test la suelte.</summary>
        public Func<object, Task<object?>?>? Interceptar { get; set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            Tokens.Add(cancellationToken);

            if (Interceptar?.Invoke(request) is { } retenida)
                return (TResponse)(await retenida)!;

            return (TResponse)Responder(request)!;
        }

        private object? Responder(object request) => request switch
        {
            ObtenerReclamacionesEnviadasQuery q => Historial(q),
            PrepararVistaPreviaReclamacionCommand q => Previa(q),
            EnviarReclamacionCommand c => AlEnviar?.Invoke(c) ?? Result.Exito(new EnvioReclamacionResultado(c.DocumentoIds, [])),
            EnviarReclamacionEmpresaCommand c => Result.Exito(new EnvioReclamacionResultado(c.DocumentoIds, [])),
            EnviarReclamacionEmpresaEmpleadoraCommand c => Result.Exito(new EnvioReclamacionResultado(c.DocumentoIds, [])),

            // Lo que la bandeja pide al montarse (mismo reparto que ComunicacionesGen2Tests.Escenario).
            ObtenerClientesParaSelectorQuery => new List<ClienteSelectorDto>(),
            ObtenerConversacionesQuery q => new ResultadoPaginado<ConversacionListaDto>(
                Conversaciones.Skip((q.Pagina - 1) * q.TamanoPagina).Take(q.TamanoPagina).ToList(), Conversaciones.Count, q.Pagina, q.TamanoPagina),
            ObtenerMensajesBuzonPersonalQuery => new List<MensajeBuzonPersonalDto>(),
            ObtenerConversacionPorIdQuery q => Conversaciones.Where(c => c.Id == q.Id).Select(DetalleDe).SingleOrDefault(),
            ObtenerMacrosQuery => new List<MacroListaDto>(),
            ObtenerCompanerosGestorCaeQuery => new List<CompaneroGestorCaeDto>(),
            ObtenerCentrosParaSelectorQuery => new List<CentroSelectorDto>(),
            ObtenerTiposDocumentoQuery => new List<TipoDocumentoListaDto>(),
            ObtenerTrabajadoresParaSelectorQuery => new List<TrabajadorSelectorDto>(),
            ObtenerTiempoGestionConversacionQuery => new TiempoGestionConversacionDto(false, 0, 0),
            ObtenerNotasInternasConversacionQuery => new List<NotaInternaDetalleDto>(),
            CaeManager.Application.Comunicaciones.AlertasCriticas.ObtenerAlertaCriticaQuery => null,
            ObtenerRecordatorioPropuestoConversacionQuery => null,
            ObtenerRespuestaSugeridaQuery => null,
            ObtenerNotaContactoQuery => null,
            _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
        };

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

    private sealed class TenantActualFalso : ITenantActual
    {
        public Guid? TenantId => null;
    }

    private sealed class NotificadorQueNadieDebeTocar : INotificadorMensajesTiempoReal
    {
        public IDisposable Suscribir(Guid tenantId, Func<MensajeWhatsAppRecibidoEvent, Task> callback) =>
            throw new NotSupportedException("Sin tenant resuelto la página no se suscribe.");

        public Task PublicarAsync(MensajeWhatsAppRecibidoEvent aviso, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AlmacenUsuariosQueNadieDebeTocar : IUserStore<ApplicationUser>
    {
        private static Exception NoDeberia() => new NotSupportedException("Sin tenant resuelto no se consulta ningún usuario.");

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
        private static Exception NoDeberia() => new NotSupportedException("Sin tenant resuelto el directorio devuelve vacío sin consultar.");

        public IQueryable<Tenant> Tenants => throw NoDeberia();
        public IQueryable<DelegacionTenant> DelegacionesTenant => throw NoDeberia();
        public IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => throw NoDeberia();
        public IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte => throw NoDeberia();
    }

    /// <summary>Mismo doble que <c>ComunicacionesGen2Tests.CrearDirectorio</c>: la bandeja lo inyecta y sin tenant no consulta nada.</summary>
    private static DirectorioUsuariosTenant CrearDirectorio()
    {
        var tenantActual = new TenantActualFalso();
        var identidad = new CaeManagerDbContext(
            new DbContextOptionsBuilder<CaeManagerDbContext>().Options,
            DataProtectionProvider.Create(nameof(HistorialReclamacionesEnComunicacionesTests)),
            tenantActual);

        var userManager = new UserManager<ApplicationUser>(
            new AlmacenUsuariosQueNadieDebeTocar(), null!, null!, null!, null!, null!, null!, null!, null!);

        return new DirectorioUsuariosTenant(
            userManager, new TenantsQueryContextQueNadieDebeTocar(), tenantActual, new PuertaAccesoDatos(), identidad, null!);
    }

    // ---------------------------------------------------------------- datos

    private static readonly DateTime FechaDeEnvio = new(2026, 11, 10, 10, 30, 0, DateTimeKind.Utc);

    private static ReclamacionEnviadaDto ReclamacionEnviada(string razonSocial = "Refrielectric SL") => new(
        Guid.NewGuid(), Guid.NewGuid(), razonSocial, AmbitoAplicacion.Cliente, "contacto@refrielectric.example",
        FechaDeEnvio, 1, null, null, [Guid.NewGuid()]);

    private static MediadorDePrueba ConHistorial(params ReclamacionEnviadaDto[] items) =>
        new() { Historial = q => new ResultadoPaginado<ReclamacionEnviadaDto>(items, items.Length, q.Pagina, q.TamanoPagina) };

    private static ConversacionListaDto Conversacion(string asunto, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), null, null, asunto, EstadoConversacion.Abierta, null, "contacto@refrielectric.example",
            "Reclamación de documentos.", DateTime.UtcNow, 1, CanalConversacion.Correo, null, DireccionMensaje.Saliente);

    private static ConversacionDetalleDto DetalleDe(ConversacionListaDto conversacion) =>
        new(conversacion.Id, conversacion.ClienteId, conversacion.ClienteRazonSocial, conversacion.Asunto,
            conversacion.Estado, null, null, conversacion.FechaUltimoMensajeUtc,
            [new MensajeDetalleDto(Guid.NewGuid(), DireccionMensaje.Saliente, CanalConversacion.Correo,
                conversacion.RemitentePrincipal, "<p>Reclamación de documentos.</p>", conversacion.FechaUltimoMensajeUtc, [], null, [])],
            [], [], []);

    // ---------------------------------------------------------------- arnés

    private (IRenderedComponent<HistorialReclamaciones> Cut, MediadorDePrueba Mediador, List<Guid> HilosAbiertos) Renderizar(
        MediadorDePrueba? mediador = null)
    {
        mediador ??= new MediadorDePrueba();
        var hilosAbiertos = new List<Guid>();

        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();

        var cut = Render<HistorialReclamaciones>(p => p
            .Add(c => c.OnVerHilo, EventCallback.Factory.Create<Guid>(this, hilosAbiertos.Add)));
        return (cut, mediador, hilosAbiertos);
    }

    private (IRenderedComponent<Bandeja> Cut, MediadorDePrueba Mediador) RenderizarBandeja(
        MediadorDePrueba? mediador = null, string url = "comunicaciones", string? rol = null)
    {
        mediador ??= new MediadorDePrueba();
        if (rol is not null) this.ConRolDeEscritura(rol);

        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddSingleton<ILogger<Bandeja>>(_ => NullLogger<Bandeja>.Instance);
        Services.AddSingleton<ILogger<RecordatorioPropuesto>>(_ => NullLogger<RecordatorioPropuesto>.Instance);
        Services.AddScoped<ITenantActual, TenantActualFalso>();
        Services.AddScoped<INotificadorMensajesTiempoReal, NotificadorQueNadieDebeTocar>();
        Services.AddScoped(_ => CrearDirectorio());
        Services.TryAddSingleton(TimeProvider.System);
        Services.AddSingleton<IOptions<ComunicacionesOptions>>(Options.Create(new ComunicacionesOptions { Activo = true }));

        // La URL se acciona antes de renderizar: es el patrón de ComunicacionesGen2Tests para los filtros que la URL manda.
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);

        return (Render<Bandeja>(), mediador);
    }

    private static IElement BotonPorTexto(IRenderedComponent<HistorialReclamaciones> cut, string texto) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == texto);

    private static IElement PastillaOrigen(IRenderedComponent<Bandeja> cut) =>
        cut.FindAll(".menu-acciones-disparador-pastilla")
            .Single(b => b.GetAttribute("aria-label") is { } nombre && nombre.StartsWith("Origen", StringComparison.Ordinal));

    private static async Task ElegirOrigenAsync(IRenderedComponent<Bandeja> cut, string opcion)
    {
        await PastillaOrigen(cut).ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitemradio]").Single(i => i.TextContent.Trim() == opcion).ClickAsync(new MouseEventArgs());
    }

    private string UrlActual() => Services.GetRequiredService<NavigationManager>().Uri;

    // ------------------------------------------------- la fila: lo que tenía main, elemento a elemento

    [Fact]
    public void La_tabla_pinta_sus_cabeceras_y_el_titulo_y_detalle_de_la_fila()
    {
        var (cut, _, _) = Renderizar(ConHistorial(ReclamacionEnviada("Refrielectric SL")));

        cut.Find("[role=table]").GetAttribute("aria-label").Should().Be("Reclamaciones enviadas");
        var cabeceras = cut.FindAll("[role=columnheader]");
        cabeceras.Select(c => c.TextContent.Trim()).Should().Equal("Conversación", "Cliente", "Respuesta", "");
        cabeceras[3].GetAttribute("aria-label").Should().Be("Acciones", "la columna de acciones tiene nombre accesible");
        cut.Find(".historial-reclamaciones-asunto").TextContent.Should().Be("Reclamación de documentos · 1 documento");
        // La fecha va en hora peninsular (noviembre, UTC+1).
        cut.Find(".historial-reclamaciones-detalle").TextContent.Trim()
            .Should().Be("Refrielectric SL · contacto@refrielectric.example · 10/11/2026 11:30");
        cut.FindAll("[role=cell]").Select(c => c.TextContent.Trim()).Should().Contain("Refrielectric SL", "columna Cliente");
    }

    [Fact]
    public void Con_varios_documentos_el_titulo_dice_cuantos()
    {
        var (cut, _, _) = Renderizar(ConHistorial(ReclamacionEnviada() with { TotalDocumentos = 3 }));

        cut.Find(".historial-reclamaciones-asunto").TextContent.Should().Be("Reclamación de documentos · 3 documentos");
    }

    [Theory]
    [InlineData(null, "Sin buzón conectado", "badge-neutro")]
    [InlineData(true, "Sin respuesta", "badge-advertencia")]
    [InlineData(false, "Respondida", "badge-exito")]
    public void El_estado_de_respuesta_se_pinta_por_su_valor(bool? sinRespuesta, string texto, string claseTono)
    {
        var (cut, _, _) = Renderizar(ConHistorial(ReclamacionEnviada() with { SinRespuesta = sinRespuesta }));

        var insignia = cut.FindAll("[role=row] [role=cell]").Select(c => c.QuerySelector(".badge")).OfType<IElement>().Single();
        insignia.TextContent.Trim().Should().Be(texto);
        insignia.ClassList.Should().Contain(claseTono);
    }

    [Fact]
    public void Reclamar_de_nuevo_solo_se_ofrece_si_la_fila_es_reenviable()
    {
        var indeterminada = ReclamacionEnviada("Indeterminada SL") with
        {
            AmbitoTitular = AmbitoAplicacion.Empresa,
            EmpresaEmpleadora = new DestinoDeReclamacionEnviadaDto(null, false),
        };
        var (cut, _, _) = Renderizar(ConHistorial(indeterminada, ReclamacionEnviada("Control SL")));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Indeterminada SL").And.Contain("Control SL"));
        cut.FindAll("button").Count(b => b.TextContent.Trim() == "Reclamar de nuevo")
            .Should().Be(1, "solo la fila de control se puede reenviar");
    }

    [Fact]
    public async Task Ver_hilo_solo_se_ofrece_con_conversacion_y_abre_esa_conversacion()
    {
        var conHilo = ReclamacionEnviada("Con hilo SL") with { ConversacionId = Guid.NewGuid() };
        var (cut, _, hilosAbiertos) = Renderizar(ConHistorial(conHilo, ReclamacionEnviada("Sin hilo SL")));

        cut.FindAll("button").Count(b => b.TextContent.Trim() == "Ver hilo").Should().Be(1);

        await BotonPorTexto(cut, "Ver hilo").ClickAsync(new MouseEventArgs());

        hilosAbiertos.Should().Equal(conHilo.ConversacionId!.Value);
    }

    // ------------------------------------------------- estados

    [Fact]
    public void Sin_reclamaciones_el_historial_dice_Nunca_reclamado_y_explica_de_donde_salen()
    {
        var (cut, _, _) = Renderizar();

        cut.Find(".estado-vacio h3").TextContent.Should().Be("Nunca reclamado");
        cut.Markup.Should().Contain("Todavía no se ha pedido nada desde TALVEG.")
            .And.Contain("Los lotes que envíes desde Documentos aparecerán aquí.");
        cut.FindAll("[role=table]").Should().BeEmpty();
    }

    [Fact]
    public void Un_fallo_al_cargar_el_historial_lo_dice_y_Reintentar_vuelve_a_pedirlo()
    {
        var falla = true;
        var mediador = new MediadorDePrueba
        {
            Historial = q => falla
                ? throw new InvalidOperationException("fallo de prueba")
                : new ResultadoPaginado<ReclamacionEnviadaDto>([ReclamacionEnviada("Recuperada SL")], 1, q.Pagina, q.TamanoPagina)
        };
        var (cut, _, _) = Renderizar(mediador);

        cut.Find(".estado-vacio h3").TextContent.Should().Be("No se ha podido cargar el historial.");

        falla = false;
        BotonPorTexto(cut, "Reintentar").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Recuperada SL"));
        mediador.Enviadas.OfType<ObtenerReclamacionesEnviadasQuery>().Should().HaveCount(2);
    }

    [Fact]
    public void Mientras_carga_se_ve_el_estado_de_carga_y_no_la_tabla()
    {
        var retenida = new TaskCompletionSource<object?>();
        var mediador = new MediadorDePrueba { Interceptar = p => p is ObtenerReclamacionesEnviadasQuery ? retenida.Task : null };

        var (cut, _, _) = Renderizar(mediador);

        cut.Find(".historial-reclamaciones-cargando").TextContent.Should().Be("Cargando…");
        cut.FindAll("[role=table]").Should().BeEmpty();

        retenida.SetResult(new ResultadoPaginado<ReclamacionEnviadaDto>([ReclamacionEnviada()], 1, 1, 20));
        cut.WaitForAssertion(() => cut.FindAll("[role=table]").Should().ContainSingle());
    }

    [Fact]
    public async Task El_paginador_pide_el_tamano_elegido_desde_la_primera_pagina()
    {
        var (cut, mediador, _) = Renderizar(ConHistorial(ReclamacionEnviada()));

        await cut.Find(".paginador-tamano-select").ChangeAsync(new ChangeEventArgs { Value = "50" });

        mediador.Enviadas.OfType<ObtenerReclamacionesEnviadasQuery>().Last().Should().Be(new ObtenerReclamacionesEnviadasQuery(1, 50));
    }

    // ------------------------------------------------- reenvío: revisión previa y confirmación

    [Fact]
    public async Task Reclamar_de_nuevo_no_envia_hasta_confirmar_y_el_aviso_dice_a_quien_llego()
    {
        var mediador = ConHistorial(ReclamacionEnviada());
        var (cut, _, _) = Renderizar(mediador);

        await BotonPorTexto(cut, "Reclamar de nuevo").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().BeEmpty("el primer clic solo abre la revisión");
        cut.Markup.Should().Contain("contacto@refrielectric.example").And.Contain("Asunto de la vista previa").And.Contain("Texto de la vista previa");
        var aviso = cut.Find(".aviso-irreversible");
        aviso.GetAttribute("role").Should().Be("note");
        aviso.TextContent.Should().Contain("Un correo enviado no se puede deshacer");

        await BotonPorTexto(cut, "Enviar reclamación").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().ContainSingle()
            .Which.ContactoIdsSeleccionados.Should().NotBeNullOrEmpty("se envía a quien enseñó la revisión");
        var toast = Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Subject;
        toast.Mensaje.Should().Be("Reclamación enviada a Contacto Refri.");
        toast.Tono.Should().Be(TonoToast.Exito);
        toast.TextoAccion.Should().BeNull("el correo ya ha salido: no hay «Deshacer»");
    }

    [Fact]
    public async Task Cancelar_en_la_revision_cierra_sin_enviar()
    {
        var mediador = ConHistorial(ReclamacionEnviada());
        var (cut, _, _) = Renderizar(mediador);

        await BotonPorTexto(cut, "Reclamar de nuevo").ClickAsync(new MouseEventArgs());
        await BotonPorTexto(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        cut.Markup.Should().NotContain("Enviar reclamación");
        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Una_vista_previa_fallida_avisa_y_no_abre_la_revision()
    {
        var mediador = ConHistorial(ReclamacionEnviada());
        mediador.Previa = _ => Result.Fallo<ReclamacionPreparada>(Error.Crear("Reclamacion.SinDestinatario", "No hay ningún contacto."));
        var (cut, _, _) = Renderizar(mediador);

        await BotonPorTexto(cut, "Reclamar de nuevo").ClickAsync(new MouseEventArgs());

        cut.Markup.Should().NotContain("Enviar reclamación");
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(t => t.Tono == TonoToast.Error);
    }

    [Fact]
    public async Task Reclamar_de_nuevo_de_titular_Empresa_usa_el_ambito_Empresa()
    {
        var fila = ReclamacionEnviada("Contraparte SL") with { AmbitoTitular = AmbitoAplicacion.Empresa };
        var mediador = ConHistorial(fila);
        var (cut, _, _) = Renderizar(mediador);

        await BotonPorTexto(cut, "Reclamar de nuevo").ClickAsync(new MouseEventArgs());
        await BotonPorTexto(cut, "Enviar reclamación").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<PrepararVistaPreviaReclamacionCommand>().Should().ContainSingle()
            .Which.Ambito.Should().Be(AmbitoAplicacion.Empresa);
        mediador.Enviadas.OfType<EnviarReclamacionEmpresaCommand>().Should().ContainSingle();
        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Reclamar_de_nuevo_lo_pedido_a_la_Empresa_empleadora_usa_su_comando_con_la_Delegacion()
    {
        var delegacionEmpresaId = Guid.NewGuid();
        var fila = ReclamacionEnviada("Contraparte SL") with
        {
            AmbitoTitular = AmbitoAplicacion.Empresa,
            EmpresaEmpleadora = new DestinoDeReclamacionEnviadaDto(delegacionEmpresaId, true),
        };
        var mediador = ConHistorial(fila);
        var (cut, _, _) = Renderizar(mediador);

        await BotonPorTexto(cut, "Reclamar de nuevo").ClickAsync(new MouseEventArgs());
        await BotonPorTexto(cut, "Enviar reclamación").ClickAsync(new MouseEventArgs());

        var envio = mediador.Enviadas.OfType<EnviarReclamacionEmpresaEmpleadoraCommand>().Should().ContainSingle().Subject;
        envio.EmpresaId.Should().Be(fila.TitularId);
        envio.DelegacionEmpresaId.Should().Be(delegacionEmpresaId);
        mediador.Enviadas.OfType<EnviarReclamacionEmpresaCommand>().Should().BeEmpty();
        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Dos_clics_en_Enviar_reclamacion_mandan_un_solo_comando()
    {
        var reclamacion = ReclamacionEnviada();
        var mediador = ConHistorial(reclamacion);
        var retenido = new TaskCompletionSource<object?>();
        mediador.Interceptar = p => p is EnviarReclamacionCommand ? retenido.Task : null;
        var (cut, _, _) = Renderizar(mediador);

        await BotonPorTexto(cut, "Reclamar de nuevo").ClickAsync(new MouseEventArgs());
        var primero = BotonPorTexto(cut, "Enviar reclamación").ClickAsync(new MouseEventArgs());
        var segundo = BotonPorTexto(cut, "Enviar reclamación").ClickAsync(new MouseEventArgs());

        await cut.InvokeAsync(() => retenido.SetResult(Result.Exito(new EnvioReclamacionResultado(reclamacion.DocumentoIds, []))));
        await primero;
        await segundo;

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().ContainSingle("el segundo clic llegó con el primero en vuelo");
    }

    // ------------------------------------------------- ciclo de vida

    [Fact]
    public async Task Retirar_el_historial_corta_la_consulta_en_vuelo_y_su_respuesta_tardia_no_pinta()
    {
        var mediador = ConHistorial(ReclamacionEnviada());
        var (cut, _, _) = Renderizar(mediador);

        mediador.Tokens.Should().NotBeEmpty().And.OnlyContain(t => t.CanBeCanceled && !t.IsCancellationRequested);

        var enVuelo = new TaskCompletionSource<object?>();
        mediador.Interceptar = p => p is ObtenerReclamacionesEnviadasQuery ? enVuelo.Task : null;
        var recarga = cut.Find(".paginador-tamano-select").ChangeAsync(new ChangeEventArgs { Value = "50" });
        var tokenEnVuelo = mediador.Tokens[^1];

        cut.Instance.Dispose();

        tokenEnVuelo.IsCancellationRequested.Should().BeTrue("retirar el historial corta lo que seguía trabajando para él");

        mediador.Interceptar = null;
        enVuelo.SetResult(new ResultadoPaginado<ReclamacionEnviadaDto>([ReclamacionEnviada("Tardía SL")], 1, 1, 50));
        await recarga;
        cut.Markup.Should().NotContain("Tardía SL");
    }

    [Fact]
    public void Retirar_el_historial_dos_veces_no_lanza()
    {
        var (cut, _, _) = Renderizar();

        var repetir = () => { cut.Instance.Dispose(); cut.Instance.Dispose(); };

        repetir.Should().NotThrow();
    }

    // ------------------------------------------------- visibilidad por rol (paridad con main)

    /// <summary>Los roles que ven el historial en main: <c>SituacionDocumentosEnCentrosService.RolesQueLeenReclamaciones</c>.</summary>
    public static TheoryData<string> RolesQueLeen() => [Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae];

    [Theory]
    [MemberData(nameof(RolesQueLeen))]
    public void Los_roles_que_leen_reclamaciones_ven_el_historial_con_sus_acciones(string rol)
    {
        this.ConRolDeEscritura(rol);
        var mediador = ConHistorial(ReclamacionEnviada());

        var (cut, _, _) = Renderizar(mediador);

        cut.FindAll("[role=table]").Should().ContainSingle();
        mediador.Enviadas.OfType<ObtenerReclamacionesEnviadasQuery>().Should().ContainSingle();
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Reclamar de nuevo");
    }

    [Theory]
    [InlineData(Roles.Consulta)]
    [InlineData(Roles.Cliente)]
    public void Consulta_y_los_roles_fuera_de_la_lista_no_ven_el_historial_ni_se_pide_al_servidor(string rol)
    {
        this.ConRolDeEscritura(rol);
        var mediador = ConHistorial(ReclamacionEnviada("No debe verse SL"));

        var (cut, _, _) = Renderizar(mediador);

        cut.FindAll("[role=table]").Should().BeEmpty();
        cut.Markup.Should().NotContain("No debe verse SL").And.Contain("Esta sección no está disponible para tu rol");
        mediador.Enviadas.Should().BeEmpty("quien no lo ve no lo pide: la pregunta ni sale");
    }

    /// <summary>
    /// La presentación del historial lee su lista de roles de <see cref="RolesComunicaciones.LecturaReclamaciones"/>. Esa lista tiene
    /// que ser la de main: la pestaña de Documentos (<c>RolesDeGestionDocumental</c>) y la query de Application
    /// (<c>RolesQueLeenReclamaciones</c>, de donde salen también los detalles de gestión). Si una cambia, esta prueba lo dice.
    /// </summary>
    [Fact]
    public void Los_roles_del_historial_son_los_que_lo_ven_en_Documentos_y_los_que_el_servidor_trata_como_lectores()
    {
        var propios = RolesComunicaciones.LecturaReclamaciones.Split(',').Order().ToArray();

        var deLaPaginaDeDocumentos = (string)typeof(CaeManager.Web.Features.Documentos.Pages.Documentos)
            .GetField("RolesDeGestionDocumental", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        var delServidor = (string[])typeof(CaeManager.Application.Documentos.SituacionEnCentro.SituacionDocumentosEnCentrosService)
            .GetField("RolesQueLeenReclamaciones", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        propios.Should().Equal(deLaPaginaDeDocumentos.Split(',').Order());
        propios.Should().Equal(delServidor.Order());
        propios.Should().NotContain(Roles.Consulta);
    }

    // ------------------------------------------------- el filtro Origen en la bandeja

    [Fact]
    public void Sin_origen_la_bandeja_no_muestra_el_historial_ni_lo_pide()
    {
        var (cut, mediador) = RenderizarBandeja();

        PastillaOrigen(cut).Should().NotBeNull("el rol de gestión ve el filtro Origen");
        cut.FindAll("[role=table]").Where(t => t.ClassList.Contains("historial-reclamaciones")).Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerReclamacionesEnviadasQuery>().Should().BeEmpty();
    }

    [Fact]
    public void Consulta_no_ve_el_filtro_Origen_y_un_enlace_con_origen_reclamaciones_se_ignora()
    {
        var (cut, mediador) = RenderizarBandeja(url: "comunicaciones?origen=reclamaciones", rol: Roles.Consulta);

        cut.FindAll(".menu-acciones-disparador-pastilla")
            .Where(b => b.GetAttribute("aria-label") is { } nombre && nombre.StartsWith("Origen", StringComparison.Ordinal))
            .Should().BeEmpty();
        cut.FindAll(".historial-reclamaciones").Should().BeEmpty();
        // Ni la tabla ni el componente: sin su cabecera ni el aviso de «sección no disponible» (ni siquiera se monta).
        cut.FindAll("[role=table]").Should().BeEmpty();
        cut.FindAll(".historial-reclamaciones-cab").Should().BeEmpty();
        cut.Markup.Should().NotContain("Esta sección no está disponible para tu rol");
        mediador.Enviadas.OfType<ObtenerReclamacionesEnviadasQuery>().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(RolesQueLeen))]
    public void Cada_rol_que_lee_reclamaciones_ve_el_filtro_Origen_en_la_bandeja(string rol)
    {
        var (cut, _) = RenderizarBandeja(rol: rol);

        PastillaOrigen(cut).Should().NotBeNull();
    }

    /// <summary>
    /// Con el historial a la vista la lista de conversaciones no se ve, pero sigue cargada: los atajos j/k/Enter de la lista no pueden
    /// abrir a ciegas una conversación que nadie está mirando. Control: sin el filtro, el mismo Enter sí la abre.
    /// </summary>
    [Theory]
    [InlineData("comunicaciones?origen=reclamaciones", false)]
    [InlineData("comunicaciones", true)]
    public async Task Los_atajos_de_la_lista_no_abren_conversaciones_con_el_historial_a_la_vista(string url, bool seAbre)
    {
        var conversacion = Conversacion("Reclamación de documentos");
        var mediador = ConHistorial();
        mediador.Conversaciones.Add(conversacion);
        var (cut, _) = RenderizarBandeja(mediador, url);
        var atajo = typeof(Bandeja).GetMethod("ManejarAtajoListaAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await cut.InvokeAsync(() => (Task)atajo.Invoke(cut.Instance, ["j"])!);
        await cut.InvokeAsync(() => (Task)atajo.Invoke(cut.Instance, ["Enter"])!);

        mediador.Enviadas.OfType<ObtenerConversacionPorIdQuery>().Any().Should().Be(seAbre);
        UrlActual().Contains("conversacion=", StringComparison.Ordinal).Should().Be(seAbre);
    }

    [Fact]
    public void Un_enlace_con_origen_reclamaciones_abre_el_historial_para_un_rol_de_gestion()
    {
        var (cut, mediador) = RenderizarBandeja(ConHistorial(ReclamacionEnviada("Refrielectric SL")), "comunicaciones?origen=reclamaciones");

        cut.WaitForAssertion(() => cut.Find(".historial-reclamaciones").TextContent.Should().Contain("Refrielectric SL"));
        cut.Find(".historial-reclamaciones-cab h2").TextContent.Trim().Should().Be("Reclamaciones");
        cut.Find(".historial-reclamaciones-cab small").TextContent.Trim().Should().Be("Lo que se ha pedido y si ha contestado.");
        mediador.Enviadas.OfType<ObtenerReclamacionesEnviadasQuery>().Should().ContainSingle();
    }

    [Fact]
    public async Task Elegir_Reclamaciones_en_Origen_muestra_el_historial_y_lo_escribe_en_la_URL_y_Todos_los_origenes_lo_quita()
    {
        var (cut, _) = RenderizarBandeja(ConHistorial(ReclamacionEnviada("Refrielectric SL")));

        await ElegirOrigenAsync(cut, "Reclamaciones");

        cut.WaitForAssertion(() => cut.Find(".historial-reclamaciones").TextContent.Should().Contain("Refrielectric SL"));
        UrlActual().Should().Contain("origen=reclamaciones");

        await ElegirOrigenAsync(cut, "Todos los orígenes");

        cut.WaitForAssertion(() => cut.FindAll(".historial-reclamaciones").Should().BeEmpty());
        UrlActual().Should().NotContain("origen=");
    }

    [Fact]
    public async Task Ver_hilo_abre_la_conversacion_en_la_bandeja_conservando_el_filtro_Origen()
    {
        var conversacion = Conversacion("Reclamación de documentos");
        var reclamacion = ReclamacionEnviada("Refrielectric SL") with { ConversacionId = conversacion.Id };
        var mediador = ConHistorial(reclamacion);
        mediador.Conversaciones.Add(conversacion);
        var (cut, _) = RenderizarBandeja(mediador, "comunicaciones?origen=reclamaciones");

        cut.WaitForAssertion(() => cut.FindAll(".historial-reclamaciones button").Should().Contain(b => b.TextContent.Trim() == "Ver hilo"));
        await cut.FindAll(".historial-reclamaciones button").Single(b => b.TextContent.Trim() == "Ver hilo").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => UrlActual().Should().Contain($"conversacion={conversacion.Id}").And.Contain("origen=reclamaciones"));
        mediador.Enviadas.OfType<ObtenerConversacionPorIdQuery>().Should().Contain(q => q.Id == conversacion.Id);
        cut.FindAll(".historial-reclamaciones").Should().BeEmpty("el hilo ocupa el sitio del historial");
    }

    // ------------------------------------------------- «Ver reclamación» del hilo (repunte)

    [Fact]
    public void Ver_reclamacion_del_hilo_apunta_al_historial_en_Comunicaciones_y_no_a_la_pestana_de_Documentos()
    {
        var evento = new EventoDetalleDto(Guid.NewGuid(), TipoEventoConversacion.ReclamacionEnviada, Guid.NewGuid(), FechaDeEnvio, "Reclamación enviada.");

        var cut = Render<UnifiedTimeline>(p => p
            .Add(c => c.Mensajes, [])
            .Add(c => c.Participantes, [])
            .Add(c => c.Eventos, [evento]));

        var enlace = cut.FindAll("a.timeline-evento-enlace").Single(a => a.TextContent.Trim() == "Ver reclamación");
        enlace.GetAttribute("href").Should().Be("/comunicaciones?origen=reclamaciones");
    }
}
