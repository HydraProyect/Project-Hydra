using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Application.Operaciones.IncorporacionCartera.Commands;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Usuarios.Commands.AsumirPrincipalDeOperacion;
using CaeManager.Application.Usuarios.Queries.ObtenerOperacionesSinPrincipal;
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
/// Bandeja de solicitudes de incorporación a cartera.
///
/// <para>
/// <b>Lo que SÍ observa:</b> qué pinta cada componente a partir de la bandeja
/// que devuelve la Query (qué filas, qué acciones), qué Command envía cada
/// botón y que tras resolver se vuelve a pedir el estado — que es lo que hace
/// desaparecer de la bandeja una solicitud que otro Coordinador CAE ya resolvió.
/// </para>
/// <para>
/// <b>Lo que NO observa:</b> la autorización (la decide la Query y cada
/// Command, probados en Application.Tests y bajo RLS en IntegrationTests), ni
/// el refresco periódico real ni la propagación entre circuitos.
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
        Services.AddSingleton<CaeManager.Application.Common.ICurrentUserService>(new UsuarioActualFalso());
    }

    private static readonly Guid Yo = Guid.NewGuid();

    /// <summary>El panel «Dar acceso» que monta la página compara este usuario con el principal de cada operación.</summary>
    private sealed class UsuarioActualFalso : CaeManager.Application.Common.ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Yo);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("GestorCae");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
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

    // -------------------------------------------------------------- Bandeja

    /// <summary>
    /// D-19 (recorrido en staging 2026-10-01): la fecha de la solicitud salía con el
    /// formato corto de la cultura («22/9/2026 12:00»), distinto del resto de la app.
    /// Formato único dd/MM/yyyy HH:mm, en hora peninsular (10:00Z en septiembre son las 12:00).
    /// </summary>
    [Fact]
    public void La_fecha_de_la_solicitud_sale_en_hora_peninsular_con_el_formato_unico()
    {
        _mediator.Bandeja = BandejaCoordinador(Solicitud("Empresa Norte", "Marta"));

        var pagina = Render<SolicitudesCartera>();

        pagina.FindAll("td").Select(c => c.TextContent.Trim()).Should().Contain("22/09/2026 12:00");
    }

    [Fact]
    public async Task Rechazar_en_la_bandeja_pide_confirmacion_y_cancelar_no_envia_el_Command()
    {
        var solicitud = Solicitud("Empresa Norte", "Marta");
        _mediator.Bandeja = BandejaCoordinador(solicitud);
        var pagina = Render<SolicitudesCartera>();

        await BotonDeFila(pagina, solicitud, Textos["Rechazar"]).ClickAsync(new());

        _mediator.Comandos.Should().BeEmpty("el rechazo se confirma antes de enviarse");
        pagina.Find(".modal-pie").TextContent.Should().Contain(Textos["Rechazar"], "control positivo: el diálogo se abrió");
        pagina.Markup.Should().Contain(Textos["ConfirmarRechazarMensaje", "Marta", "Empresa Norte"]);

        await BotonDelDialogo(pagina, "Cancelar").ClickAsync(new());

        _mediator.Comandos.Should().BeEmpty();
        pagina.FindAll(".modal-pie").Should().BeEmpty("cancelar cierra el diálogo");
    }

    [Fact]
    public async Task Rechazar_en_la_bandeja_y_confirmar_envia_el_Command_de_esa_solicitud()
    {
        var solicitud = Solicitud("Empresa Norte", "Marta");
        _mediator.Bandeja = BandejaCoordinador(solicitud);
        var pagina = Render<SolicitudesCartera>();

        await BotonDeFila(pagina, solicitud, Textos["Rechazar"]).ClickAsync(new());
        await BotonDelDialogo(pagina, Textos["Rechazar"]).ClickAsync(new());

        _mediator.Comandos.Should().ContainSingle()
            .Which.Should().Be(new RechazarSolicitudIncorporacionCarteraCommand(solicitud.Id));
        Toasts.Mensajes.Should().ContainSingle(t => t.Tono == TonoToast.Exito && t.Mensaje == Textos["ToastRechazada"].Value);
        pagina.FindAll(".modal-pie").Should().BeEmpty();
    }

    private static AngleSharp.Dom.IElement BotonDeFila<T>(IRenderedComponent<T> cut, SolicitudIncorporacionCarteraDto solicitud, string texto)
        where T : IComponent =>
        cut.FindAll($"[data-solicitud='{solicitud.Id}'] button").Single(b => b.TextContent.Trim() == texto);

    private static AngleSharp.Dom.IElement BotonDelDialogo<T>(IRenderedComponent<T> cut, string texto) where T : IComponent =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == texto);

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
    public void La_pagina_ofrece_Dar_acceso_solo_en_los_Tenants_de_los_que_quien_mira_es_principal()
    {
        static CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera.CarterasDeOperacion Operacion(string tenant, Guid principal) =>
            new(Guid.NewGuid(), Guid.NewGuid(), tenant,
                new CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera.PersonaConCartera(principal, "Alguien", "GestorCae", null), []);

        // «Dar acceso» va dentro de SoloConEscritura: hace falta sesión con un rol que escriba.
        var sesion = AddAuthorization();
        sesion.SetAuthorized("yo");
        sesion.SetRoles("GestorCae");

        _mediator.Bandeja = new BandejaIncorporacionCarteraDto(EsCoordinadorCae: false, [], [], []);
        _mediator.Carteras = [Operacion("Empresa Mía", Yo), Operacion("Empresa Ajena", Guid.NewGuid())];

        var pagina = Render<SolicitudesCartera>();

        var panel = pagina.Find("[data-testid=panel-dar-acceso]");
        panel.TextContent.Should().Contain("Empresa Mía").And.NotContain("Empresa Ajena");
        panel.QuerySelectorAll("[data-dar-acceso-operacion] > button").Should().ContainSingle()
            .Which.TextContent.Should().Contain("Dar acceso");
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
    public void La_campana_del_layout_no_queda_tras_una_puerta_de_rol()
    {
        var layout = LeerWeb(Path.Combine("Components", "Layout", "MainLayout.razor"));
        var posicion = layout.IndexOf("Notificaciones.CampanaAvisos", StringComparison.Ordinal);
        posicion.Should().BePositive("la campana debe estar montada en el layout");

        // AuthorizeView abiertos y sin cerrar antes de la campana: los que lo envuelven.
        var abiertos = new Stack<string>();
        foreach (Match m in Regex.Matches(layout[..posicion], @"<AuthorizeView\b[^>]*>|</AuthorizeView>"))
        {
            if (m.Value.StartsWith("</", StringComparison.Ordinal))
                abiertos.Pop();
            else
                abiertos.Push(m.Value);
        }

        abiertos.Should().NotBeEmpty("la campana sigue exigiendo sesión (control positivo)");
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

    // ------------------------------------------------- Alerta «sin principal»

    private static OperacionEnAlertaDePrincipal EnAlerta(
        string nombre, SituacionDePrincipal situacion, int personas = 0, string? principal = null, bool propia = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), nombre, situacion, personas, principal, propia);

    private static IElement BotonAsumir(IRenderedComponent<SolicitudesCartera> pagina, OperacionEnAlertaDePrincipal operacion) =>
        pagina.Find($"table[data-sin-principal] tr[data-operacion='{operacion.AsignacionOperacionId}'] button");

    [Fact]
    public void La_bandeja_lista_las_empresas_sin_principal_con_su_situacion_y_el_boton_Asumir()
    {
        var sinNadie = EnAlerta("Talleres Norte", SituacionDePrincipal.SinNadieAsignado);
        var conApoyo = EnAlerta("Obras Sur", SituacionDePrincipal.ConPersonasSinPrincipal, personas: 2);
        var conCoordinador = EnAlerta("Montajes Este", SituacionDePrincipal.CoordinadorCaePrincipal, 1, "Carla");
        _mediator.Alerta = new AlertaDePrincipal(true, [sinNadie, conApoyo, conCoordinador]);

        var pagina = Render<SolicitudesCartera>();

        var filas = pagina.FindAll("table[data-sin-principal] tbody tr");
        filas.Select(f => f.GetAttribute("data-operacion")).Should().Equal(
            sinNadie.AsignacionOperacionId.ToString(), conApoyo.AsignacionOperacionId.ToString());
        filas[0].TextContent.Should().Contain("Talleres Norte").And.Contain(Textos["SinPrincipalSinNadie"]);
        filas[1].TextContent.Should().Contain("Obras Sur").And.Contain(Textos["SinPrincipalConApoyoVarias", 2]);
        BotonAsumir(pagina, sinNadie).TextContent.Trim().Should().Be(Textos["SinPrincipalAsumir"]);

        // La que ya tiene un Coordinador CAE principal es informativa: sin botón.
        var informativa = pagina.Find("table[data-coordinador-principal] tbody tr");
        informativa.TextContent.Should().Contain("Montajes Este").And.Contain("Carla");
        pagina.FindAll("table[data-coordinador-principal] button").Should().BeEmpty();
    }

    [Fact]
    public async Task Asumir_envia_el_Command_de_esa_operacion_sin_confirmacion_y_recarga_la_alerta()
    {
        var operacion = EnAlerta("Talleres Norte", SituacionDePrincipal.SinNadieAsignado);
        _mediator.Alerta = new AlertaDePrincipal(true, [operacion]);
        var pagina = Render<SolicitudesCartera>();
        _mediator.Alerta = new AlertaDePrincipal(true, []);

        await BotonAsumir(pagina, operacion).ClickAsync(new());

        _mediator.Comandos.Should().ContainSingle()
            .Which.Should().Be(new AsumirPrincipalDeOperacionCommand(operacion.AsignacionOperacionId));
        Toasts.Mensajes.Should().ContainSingle(t => t.Tono == TonoToast.Exito
            && t.Mensaje == Textos["SinPrincipalAsumida", "Talleres Norte"].Value);
        _mediator.ConsultasDeAlerta.Should().Be(2);
        pagina.FindAll("table[data-sin-principal]").Should().BeEmpty("la empresa asumida sale de la lista");
    }

    [Fact]
    public async Task Si_otra_persona_la_asumio_antes_se_dice_y_la_lista_se_recarga()
    {
        var operacion = EnAlerta("Talleres Norte", SituacionDePrincipal.SinNadieAsignado);
        _mediator.Alerta = new AlertaDePrincipal(true, [operacion]);
        _mediator.AlComando = _ => Result.Fallo(AsumirPrincipalDeOperacionCommandHandler.YaTienePrincipal);
        var pagina = Render<SolicitudesCartera>();

        await BotonAsumir(pagina, operacion).ClickAsync(new());

        Toasts.Mensajes.Should().ContainSingle(t => t.Tono == TonoToast.Error
            && t.Mensaje == Textos["ErrorAsumirYaTienePrincipal"].Value);
        _mediator.ConsultasDeAlerta.Should().Be(2);
    }

    [Theory]
    [InlineData("es-ES")]
    [InlineData("ca-ES")]
    public void El_aviso_de_exito_de_Asumir_no_afirma_un_rol(string cultura)
    {
        // Quien ya tenía cartera de Gestor CAE la conserva marcada: decirle «Coordinador CAE» sería falso.
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(cultura);

        Textos["SinPrincipalAsumida", "Talleres Norte"].Value.Should().Contain("Talleres Norte")
            .And.NotContain("Coordinador").And.NotContain("Gestor");
    }

    [Fact]
    public void Cada_error_de_Asumir_tiene_su_texto_en_los_dos_idiomas()
    {
        Error[] errores =
        [
            AsumirPrincipalDeOperacionCommandHandler.SinAutoridad,
            AsumirPrincipalDeOperacionCommandHandler.OperacionNoEncontrada,
            AsumirPrincipalDeOperacionCommandHandler.YaTienePrincipal,
            AsumirPrincipalDeOperacionCommandHandler.NoSePudoAsignar,
            AsumirPrincipalDeOperacionCommandHandler.CambioMientrasDecidias,
        ];

        foreach (var cultura in new[] { "es-ES", "ca-ES" })
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(cultura);
            foreach (var error in errores)
                Textos[$"ErrorAsumir{error.Codigo["AsumirPrincipal.".Length..]}"].ResourceNotFound
                    .Should().BeFalse($"{error.Codigo} debe tener texto en {cultura}");
        }
    }

    [Fact]
    public void Direccion_CAE_y_Administrador_ven_la_alerta_en_vez_de_el_aviso_de_sin_acceso()
    {
        _mediator.FalloBandeja = ErroresSolicitudCartera.SinPermiso;
        _mediator.Alerta = new AlertaDePrincipal(true, [EnAlerta("Talleres Norte", SituacionDePrincipal.SinNadieAsignado)]);

        var pagina = Render<SolicitudesCartera>();

        pagina.FindAll("table[data-sin-principal] tbody tr").Should().ContainSingle();
        pagina.Markup.Should().NotContain(Textos["SinAccesoTitulo"]);
    }

    [Fact]
    public void Con_la_alerta_vacia_Direccion_CAE_y_Administrador_leen_que_todo_tiene_principal()
    {
        _mediator.FalloBandeja = ErroresSolicitudCartera.SinPermiso;
        _mediator.Alerta = new AlertaDePrincipal(true, []);

        var pagina = Render<SolicitudesCartera>();

        pagina.Markup.Should().Contain(Textos["SinPrincipalVacioTitulo"]).And.NotContain(Textos["SinAccesoTitulo"]);
    }

    [Fact]
    public void Quien_no_ve_la_alerta_ni_tiene_bandeja_sigue_leyendo_que_no_tiene_acceso()
    {
        _mediator.FalloBandeja = ErroresSolicitudCartera.SinPermiso;

        var pagina = Render<SolicitudesCartera>();

        pagina.Markup.Should().Contain(Textos["SinAccesoTitulo"]);
        pagina.FindAll("table[data-sin-principal]").Should().BeEmpty();
    }

    private sealed class MediatorFalso : IMediator
    {
        public BandejaIncorporacionCarteraDto Bandeja { get; set; } = BandejaCoordinador();
        public Error? FalloBandeja { get; set; }
        public Func<object, Result> AlComando { get; set; } = _ => Result.Exito();
        public AlertaDePrincipal Alerta { get; set; } = AlertaDePrincipal.Ninguna;
        public int ConsultasDeAlerta { get; private set; }
        public List<ObtenerSolicitudesIncorporacionCarteraQuery> Consultas { get; } = [];
        public List<object> Comandos { get; } = [];

        public IReadOnlyList<CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera.CarterasDeOperacion> Carteras { get; set; } = [];

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
                case ObtenerOperacionesSinPrincipalQuery:
                    ConsultasDeAlerta++;
                    respuesta = Alerta;
                    break;
                case AceptarSolicitudIncorporacionCarteraCommand or RechazarSolicitudIncorporacionCarteraCommand
                    or RevocarIncorporacionCarteraCommand or AsumirPrincipalDeOperacionCommand:
                    Comandos.Add(request);
                    respuesta = AlComando(request);
                    break;
                // La página monta el panel «Dar acceso», que pregunta por las carteras del
                // Operador CAE; vacío, el panel no pinta nada (lo cubre PanelDarAccesoTests).
                case CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera.ObtenerPersonasConCarteraQuery:
                    respuesta = Carteras;
                    break;
                case CaeManager.Application.Operaciones.ApoyoCartera.Queries.ObtenerPropuestasApoyoPendientesQuery:
                    respuesta = CaeManager.Application.Operaciones.ApoyoCartera.Queries.PropuestasApoyoPendientesDto.Vacia;
                    break;
                // Y por los apoyos que quien mira puede terminar; vacío, tampoco pinta nada.
                case CaeManager.Application.Operaciones.ApoyoCartera.Queries.ObtenerApoyosDeCarteraQuery:
                    respuesta = CaeManager.Application.Operaciones.ApoyoCartera.Queries.ApoyosDeCarteraDto.Vacio;
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
