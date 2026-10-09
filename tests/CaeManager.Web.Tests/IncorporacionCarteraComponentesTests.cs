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
        await BotonDelDialogo(aviso, Textos["Rechazar"]).ClickAsync(new());

        _mediator.Comandos.Should().ContainSingle()
            .Which.Should().Be(new RechazarSolicitudIncorporacionCarteraCommand(solicitud.Id));
        Toasts.Mensajes.Should().ContainSingle(t =>
            t.Tono == TonoToast.Error && t.Mensaje == Textos["ErrorYaResuelta"].Value);
        aviso.FindAll("[data-solicitud]").Select(e => e.GetAttribute("data-solicitud"))
            .Should().Equal(otra.Id.ToString());
    }

    [Fact]
    public async Task Rechazar_en_el_aviso_pide_confirmacion_y_cancelar_no_envia_el_Command()
    {
        var solicitud = Solicitud("Empresa Norte", "Marta");
        _mediator.Bandeja = BandejaCoordinador(solicitud);
        var aviso = Render<AvisoSolicitudesCartera>();

        await BotonDeFila(aviso, solicitud, Textos["Rechazar"]).ClickAsync(new());

        _mediator.Comandos.Should().BeEmpty("el rechazo se confirma antes de enviarse");
        aviso.Find(".modal-pie").TextContent.Should().Contain(Textos["Rechazar"], "control positivo: el diálogo se abrió");
        aviso.Markup.Should().Contain(Textos["ConfirmarRechazarMensaje", "Marta", "Empresa Norte"]);

        await BotonDelDialogo(aviso, "Cancelar").ClickAsync(new());

        _mediator.Comandos.Should().BeEmpty();
        aviso.FindAll(".modal-pie").Should().BeEmpty("cancelar cierra el diálogo");
        aviso.FindAll("[data-solicitud]").Should().ContainSingle("la solicitud sigue pendiente");
    }

    [Fact]
    public async Task Rechazar_en_el_aviso_y_confirmar_envia_el_Command_de_esa_solicitud()
    {
        var solicitud = Solicitud("Empresa Norte", "Marta");
        _mediator.Bandeja = BandejaCoordinador(solicitud);
        var aviso = Render<AvisoSolicitudesCartera>();
        _mediator.Bandeja = BandejaCoordinador();

        await BotonDeFila(aviso, solicitud, Textos["Rechazar"]).ClickAsync(new());
        await BotonDelDialogo(aviso, Textos["Rechazar"]).ClickAsync(new());

        _mediator.Comandos.Should().ContainSingle()
            .Which.Should().Be(new RechazarSolicitudIncorporacionCarteraCommand(solicitud.Id));
        Toasts.Mensajes.Should().ContainSingle(t => t.Tono == TonoToast.Exito && t.Mensaje == Textos["ToastRechazada"].Value);
        aviso.FindAll(".modal-pie").Should().BeEmpty();
    }

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

    [Fact]
    public void Cada_error_de_Asumir_tiene_su_texto_en_los_dos_idiomas()
    {
        Error[] errores =
        [
            AsumirPrincipalDeOperacionCommandHandler.SinAutoridad,
            AsumirPrincipalDeOperacionCommandHandler.OperacionNoEncontrada,
            AsumirPrincipalDeOperacionCommandHandler.YaTienePrincipal,
            AsumirPrincipalDeOperacionCommandHandler.AccesoDeOtroTipo,
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
