using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Bunit;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Application.Operaciones.IncorporacionCartera.Commands;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.IncorporacionCartera.Components;
using CaeManager.Web.Features.IncorporacionCartera.Pages;
using CaeManager.Web.Features.IncorporacionCartera.Recursos;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Aviso emergente y bandeja de solicitudes de incorporación a cartera.
///
/// <para>
/// <b>Lo que SÍ observa:</b> qué pinta cada componente a partir de la bandeja
/// que devuelve la Query (qué filas, qué acciones), qué Command envía cada
/// botón y que tras resolver se vuelve a pedir el estado — que es lo que hace
/// desaparecer del aviso una solicitud que otro Coordinador CAE ya resolvió.
/// </para>
/// <para>
/// <b>Lo que NO observa:</b> la autorización (la decide la Query y cada
/// Command, probados en Application.Tests y bajo RLS en IntegrationTests), ni
/// el refresco periódico real (60 s) ni la propagación entre circuitos.
/// </para>
/// </summary>
public class IncorporacionCarteraComponentesTests : BunitContext
{
    private readonly MediatorFalso _mediator = new();

    public IncorporacionCarteraComponentesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddSingleton<IMediator>(_mediator);
        Services.AddSingleton<ToastService>();
        Services.AddSingleton<ILogger<ExcepcionDeCircuitoDesconectado>>(NullLogger<ExcepcionDeCircuitoDesconectado>.Instance);
    }

    private IStringLocalizer<TextosIncorporacionCartera> Textos =>
        Services.GetRequiredService<IStringLocalizer<TextosIncorporacionCartera>>();

    private ToastService Toasts => Services.GetRequiredService<ToastService>();

    private static SolicitudIncorporacionCarteraDto Solicitud(
        string empresa,
        string gestor,
        bool puedeResolver = true,
        bool puedeRevocar = false,
        EstadoSolicitudIncorporacionCartera estado = EstadoSolicitudIncorporacionCartera.Pendiente) =>
        new(Guid.NewGuid(), Guid.NewGuid(), empresa, Guid.NewGuid(), gestor, $"Mensaje de {gestor}", estado,
            new DateTime(2026, 9, 22, 10, 0, 0, DateTimeKind.Utc), null, null, puedeResolver, puedeRevocar);

    private static BandejaIncorporacionCarteraDto BandejaCoordinador(params SolicitudIncorporacionCarteraDto[] pendientes) =>
        new(true, pendientes, [], []);

    // ---------------------------------------------------------------- Aviso

    [Fact]
    public void El_aviso_lista_las_pendientes_que_puede_resolver_y_omite_la_propia()
    {
        var ajena = Solicitud("Empresa Norte", "Marta");
        var propia = Solicitud("Empresa Sur", "Yo mismo", puedeResolver: false);
        _mediator.Bandeja = BandejaCoordinador(ajena, propia);

        var aviso = Render<AvisoSolicitudesCartera>();

        aviso.FindAll("[data-solicitud]").Select(e => e.GetAttribute("data-solicitud"))
            .Should().Equal(ajena.Id.ToString());
        aviso.Markup.Should().Contain(Textos["AvisoPide", "Marta", "Empresa Norte"]);
        aviso.Markup.Should().Contain(Textos["AvisoResumenUna", 1]);
        _mediator.Consultas.Should().ContainSingle().Which.SoloPendientes.Should().BeTrue();
    }

    [Fact]
    public void Sin_pendientes_resolubles_el_aviso_no_se_pinta()
    {
        _mediator.Bandeja = BandejaCoordinador(Solicitud("Empresa Sur", "Yo mismo", puedeResolver: false));

        Render<AvisoSolicitudesCartera>().Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Si_la_Query_niega_el_permiso_el_aviso_no_se_pinta()
    {
        _mediator.FalloBandeja = ErroresSolicitudCartera.SinPermiso;

        Render<AvisoSolicitudesCartera>().Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Una_bandeja_de_Gestor_CAE_no_abre_el_aviso_aunque_traiga_pendientes()
    {
        _mediator.Bandeja = new BandejaIncorporacionCarteraDto(false, [Solicitud("Empresa Norte", "Marta")], [], []);

        Render<AvisoSolicitudesCartera>().Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void En_la_propia_bandeja_el_aviso_no_se_pinta()
    {
        _mediator.Bandeja = BandejaCoordinador(Solicitud("Empresa Norte", "Marta"));
        Services.GetRequiredService<NavigationManager>().NavigateTo(RutasIncorporacionCartera.Bandeja);

        Render<AvisoSolicitudesCartera>().Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Mas_tarde_oculta_el_aviso()
    {
        _mediator.Bandeja = BandejaCoordinador(Solicitud("Empresa Norte", "Marta"));
        var aviso = Render<AvisoSolicitudesCartera>();

        aviso.FindAll("button").Single(b => b.TextContent.Trim() == Textos["AvisoMasTarde"]).Click();

        aviso.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public async Task Aceptar_envia_el_Command_de_esa_solicitud_avisa_y_recarga()
    {
        var solicitud = Solicitud("Empresa Norte", "Marta");
        _mediator.Bandeja = BandejaCoordinador(solicitud);
        var aviso = Render<AvisoSolicitudesCartera>();
        _mediator.Bandeja = BandejaCoordinador();

        await aviso.Find($"[data-solicitud='{solicitud.Id}'] button").ClickAsync(new());

        _mediator.Comandos.Should().ContainSingle()
            .Which.Should().Be(new AceptarSolicitudIncorporacionCarteraCommand(solicitud.Id));
        Toasts.Mensajes.Should().ContainSingle(t =>
            t.Tono == TonoToast.Exito && t.Mensaje == Textos["ToastAceptada", "Marta", "Empresa Norte"]);
        _mediator.Consultas.Should().HaveCount(2);
        aviso.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public async Task Si_otro_Coordinador_CAE_ya_la_resolvio_se_explica_y_desaparece()
    {
        var solicitud = Solicitud("Empresa Norte", "Marta");
        var otra = Solicitud("Empresa Este", "Luis");
        _mediator.Bandeja = BandejaCoordinador(solicitud, otra);
        _mediator.AlComando = _ => Result.Fallo(ErroresSolicitudCartera.YaResuelta);
        var aviso = Render<AvisoSolicitudesCartera>();
        _mediator.Bandeja = BandejaCoordinador(otra);

        var rechazar = aviso.FindAll($"[data-solicitud='{solicitud.Id}'] button")
            .Single(b => b.TextContent.Trim() == Textos["Rechazar"]);
        await rechazar.ClickAsync(new());

        _mediator.Comandos.Should().ContainSingle()
            .Which.Should().Be(new RechazarSolicitudIncorporacionCarteraCommand(solicitud.Id));
        Toasts.Mensajes.Should().ContainSingle(t =>
            t.Tono == TonoToast.Error && t.Mensaje == Textos["ErrorYaResuelta"].Value);
        aviso.FindAll("[data-solicitud]").Select(e => e.GetAttribute("data-solicitud"))
            .Should().Equal(otra.Id.ToString());
    }

    // -------------------------------------------------------------- Bandeja

    [Fact]
    public void La_bandeja_del_Coordinador_CAE_no_ofrece_resolver_su_propia_solicitud()
    {
        var ajena = Solicitud("Empresa Norte", "Marta");
        var propia = Solicitud("Empresa Sur", "Yo mismo", puedeResolver: false);
        _mediator.Bandeja = BandejaCoordinador(ajena, propia);

        var pagina = Render<SolicitudesCartera>();

        pagina.FindAll($"[data-solicitud='{ajena.Id}'] button").Select(b => b.TextContent.Trim())
            .Should().Equal(Textos["Aceptar"], Textos["Rechazar"]);
        pagina.FindAll($"[data-solicitud='{propia.Id}'] button").Should().BeEmpty();
        pagina.Find($"[data-solicitud='{propia.Id}']").TextContent.Should().Contain(Textos["EsTuSolicitud"]);
    }

    [Fact]
    public void La_bandeja_del_Gestor_CAE_solo_ofrece_revocar_lo_que_la_Query_permite()
    {
        var aceptada = Solicitud("Empresa Norte", "Yo", puedeResolver: false, puedeRevocar: true,
            estado: EstadoSolicitudIncorporacionCartera.Aceptada);
        var rechazada = Solicitud("Empresa Sur", "Yo", puedeResolver: false,
            estado: EstadoSolicitudIncorporacionCartera.Rechazada);
        _mediator.Bandeja = new BandejaIncorporacionCarteraDto(false, [], [], [aceptada, rechazada]);

        var pagina = Render<SolicitudesCartera>();

        pagina.FindAll($"[data-solicitud='{aceptada.Id}'] button").Select(b => b.TextContent.Trim())
            .Should().Equal(Textos["Revocar"]);
        pagina.FindAll($"[data-solicitud='{rechazada.Id}'] button").Should().BeEmpty();
        pagina.Find($"[data-solicitud='{rechazada.Id}']").TextContent.Should().Contain(Textos["EstadoRechazada"]);
    }

    // ------------------------------------------------- Puertas por rol de origen
    //
    // Hallazgo de Codex (P1): dentro de un Workspace operativo derivado el claim de rol es el de
    // la cartera en ese Tenant propietario. Ninguna puerta de la interfaz puede decidir por
    // IsInRole quién participa: lo deciden ParticipaEnIncorporacionCarteraQuery y la bandeja, con
    // el rol en el Operador CAE de origen (probado en Application.Tests).

    [Fact]
    public void La_bandeja_sin_permiso_explica_para_quien_es_y_no_ofrece_reintentar()
    {
        _mediator.FalloBandeja = ErroresSolicitudCartera.SinPermiso;

        var pagina = Render<SolicitudesCartera>();

        pagina.Markup.Should().Contain(Textos["SinAccesoTitulo"]).And.NotContain(Textos["ErrorCargaTitulo"]);
        pagina.FindAll("button").Should().BeEmpty();
    }

    [Fact]
    public void Otro_fallo_de_la_bandeja_sigue_siendo_un_error_de_carga()
    {
        _mediator.FalloBandeja = ErroresSolicitudCartera.SinTenantDeOrigen;

        var pagina = Render<SolicitudesCartera>();

        pagina.Markup.Should().Contain(Textos["ErrorCargaTitulo"]).And.NotContain(Textos["SinAccesoTitulo"]);
    }

    [Fact]
    public void La_pagina_no_filtra_por_el_claim_de_rol()
    {
        var autorizaciones = typeof(SolicitudesCartera).GetCustomAttributes<AuthorizeAttribute>().ToList();

        autorizaciones.Should().ContainSingle("la página exige sesión (control positivo)");
        autorizaciones.Should().OnlyContain(a => a.Roles == null && a.Policy == null);
    }

    [Theory]
    [InlineData("CoordinadorCae", false, false)]
    [InlineData("GestorCae", false, false)]
    [InlineData("Consulta", true, true)]
    [InlineData("Administrador", true, true)]
    public void El_enlace_del_menu_sigue_a_la_Query_no_al_claim(string claim, bool participa, bool visible)
    {
        var usuario = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, claim)], "Prueba"));
        var contexto = new ContextoMenuLateral(usuario, null, true, false, PerfilVocabularioTenant.Consultora, true,
            ParticipaEnIncorporacionCartera: participa);

        var enlaces = CatalogoMenuLateral.Visibles(contexto).SelectMany(g => g.Enlaces).Select(e => e.Id).ToList();

        enlaces.Should().Contain("dashboard", "el grupo del enlace debe verse para que el caso diga algo");
        enlaces.Contains("solicitudes-cartera").Should().Be(visible);
    }

    [Fact]
    public void El_aviso_del_layout_no_queda_tras_una_puerta_de_rol()
    {
        var layout = LeerWeb(Path.Combine("Components", "Layout", "MainLayout.razor"));
        var posicion = layout.IndexOf("IncorporacionCartera.Components.AvisoSolicitudesCartera", StringComparison.Ordinal);
        posicion.Should().BePositive("el aviso debe estar montado en el layout");

        // AuthorizeView abiertos y sin cerrar antes del aviso: los que lo envuelven.
        var abiertos = new Stack<string>();
        foreach (Match m in Regex.Matches(layout[..posicion], @"<AuthorizeView\b[^>]*>|</AuthorizeView>"))
        {
            if (m.Value.StartsWith("</", StringComparison.Ordinal))
                abiertos.Pop();
            else
                abiertos.Push(m.Value);
        }

        abiertos.Should().NotBeEmpty("el aviso sigue exigiendo sesión (control positivo)");
        abiertos.Should().OnlyContain(etiqueta => !etiqueta.Contains("Roles") && !etiqueta.Contains("Policy"));
    }

    private static string LeerWeb(string relativa)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer el layout");
        return File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", relativa));
    }

    private sealed class MediatorFalso : IMediator
    {
        public BandejaIncorporacionCarteraDto Bandeja { get; set; } = BandejaCoordinador();
        public Error? FalloBandeja { get; set; }
        public Func<object, Result> AlComando { get; set; } = _ => Result.Exito();
        public List<ObtenerSolicitudesIncorporacionCarteraQuery> Consultas { get; } = [];
        public List<object> Comandos { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta;
            switch (request)
            {
                case ObtenerSolicitudesIncorporacionCarteraQuery consulta:
                    Consultas.Add(consulta);
                    respuesta = FalloBandeja is { } error
                        ? Result.Fallo<BandejaIncorporacionCarteraDto>(error)
                        : Result.Exito(Bandeja);
                    break;
                case AceptarSolicitudIncorporacionCarteraCommand or RechazarSolicitudIncorporacionCarteraCommand
                    or RevocarIncorporacionCarteraCommand:
                    Comandos.Add(request);
                    respuesta = AlComando(request);
                    break;
                default:
                    throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");
            }

            return Task.FromResult((TResponse)respuesta);
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
}
