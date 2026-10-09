using CaeManager.Application.Usuarios.Queries.ObtenerAvatarPropio;
using System.Security.Claims;
using Bunit;
using CaeManager.Application.Notificaciones.Commands.MarcarNotificacionLeida;
using CaeManager.Application.Notificaciones.Queries.ObtenerNotificacionesPendientes;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Queries.UsaRotulosPrimeraPersona;
using CaeManager.Application.Usuarios.Queries.ObtenerOperacionesSinPrincipal;
using CaeManager.Application.VigilanciaNormativa.Queries.ObtenerAvisosRevisionNormativa;
using CaeManager.Application.VistaDemo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.Notificaciones;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Opciones = Microsoft.Extensions.Options.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// El rediseño de la cabecera (menú de usuario, chip de vistas, campana de avisos, migas): lo que
/// cada pieza pinta, qué fuente lee y qué NO hace. El comportamiento que vive en
/// <c>wwwroot/js/cabecera.js</c> (apertura, Esc, clic fuera, sombra al desplazar) lo cubre el E2E
/// <c>CabeceraTests</c>: bUnit no ejecuta ese script.
/// </summary>
public class CabeceraRedisenadaTests : BunitContext
{
    private static readonly Guid Marta = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public CabeceraRedisenadaTests()
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton<ILogger<ExcepcionDeCircuitoDesconectado>>(NullLogger<ExcepcionDeCircuitoDesconectado>.Instance);
        Services.AddSingleton<AntiforgeryStateProvider>(new AntiforgeryFalso());
        Services.AddSingleton<ILogger<MenuUsuario>>(NullLogger<MenuUsuario>.Instance);
    }

    // ---- Iniciales del avatar ----

    [Theory]
    [InlineData("Elena Ríos", "ER")]
    [InlineData("elena.rios@arcos.es", "ER")]
    [InlineData("marta", "M")]
    [InlineData("  ", "?")]
    [InlineData(null, "?")]
    [InlineData("123 456", "?")]
    public void Las_iniciales_salen_del_nombre_y_no_del_dominio_del_correo(string? nombre, string esperado) =>
        InicialesDeUsuario.De(nombre).Should().Be(esperado);

    // ---- Menú de usuario (propuesta 01) ----

    [Fact]
    public void El_menu_de_usuario_es_un_menu_con_firma_idioma_y_cierre_de_sesion_y_nace_cerrado()
    {
        Services.AddSingleton<IOptions<OpcionesLocalizacion>>(
            Opciones.Create(new OpcionesLocalizacion { CatalanHabilitado = true }));

        Services.AddSingleton<IMediator>(new MediatorFalso(_ => null));

        var cut = Render<MenuUsuario>(p => p.Add(m => m.Nombre, "Elena Ríos"));

        var boton = cut.Find("#menu-cuenta-boton");
        boton.GetAttribute("aria-haspopup").Should().Be("menu");
        boton.GetAttribute("aria-expanded").Should().Be("false", "nace cerrado; lo abre cabecera.js");
        boton.GetAttribute("aria-controls").Should().Be("menu-cuenta-panel");
        boton.TextContent.Trim().Should().Be("ER");
        boton.GetAttribute("aria-label").Should().Be("Menú de usuario", "el avatar son iniciales: el nombre accesible no puede ser ese texto");

        var panel = cut.Find("#menu-cuenta-panel");
        panel.GetAttribute("role").Should().Be("menu");
        panel.ClassList.Should().NotContain("abierto");
        panel.QuerySelector(".menu-cuenta-nombre")!.TextContent.Trim().Should().Be("Elena Ríos");

        var enlaces = panel.QuerySelectorAll("a[role=menuitem]").Select(a => a.GetAttribute("href")).ToList();
        enlaces.Should().Equal(["/mi-avatar", "/mi-firma"],
            "el propio avatar, arriba del todo, lleva a elegirlo; después «Mi firma y mis datos»");

        panel.QuerySelector("form.selector-idioma-formulario")!.GetAttribute("action").Should().Be("/cuenta/idioma",
            "el idioma sigue siendo el <form> POST de siempre, ahora dentro del menú");

        var salida = panel.QuerySelectorAll("form").Single(f => f.GetAttribute("action") == "/cuenta/cerrar-sesion");
        salida.GetAttribute("method").Should().Be("post");
        var cerrar = salida.QuerySelector("button.boton-cerrar-sesion")!;
        cerrar.GetAttribute("role").Should().Be("menuitem");
        cerrar.TextContent.Trim().Should().Be("Cerrar sesión");
    }

    [Fact]
    public void Con_el_catalan_apagado_el_menu_no_ofrece_idioma()
    {
        Services.AddSingleton<IOptions<OpcionesLocalizacion>>(Opciones.Create(new OpcionesLocalizacion()));
        Services.AddSingleton<IMediator>(new MediatorFalso(_ => null));

        var cut = Render<MenuUsuario>(p => p.Add(m => m.Nombre, "Elena Ríos"));

        cut.FindAll("form.selector-idioma-formulario").Should().BeEmpty();
        cut.FindAll("form").Should().ContainSingle(f => f.GetAttribute("action") == "/cuenta/cerrar-sesion");
    }

    // ---- Chip de vistas técnicas (propuesta 05) ----

    private void Autenticar(params string[] roles) =>
        AddAuthorization().SetAuthorized("elena").SetRoles(roles);

    private void RegistrarVistaDemo(bool disponible, VistaDemoEfectiva? efectiva = null)
    {
        Services.AddSingleton<IVistaDemoActual>(new VistaDemoFalsa(disponible, efectiva));
        Services.AddSingleton<IVistaVocabularioPreviewService>(new VistaPreviaFalsa());
    }

    [Fact]
    public void Sin_vista_de_demo_ni_Administrador_no_hay_chip()
    {
        Autenticar(Roles.Consulta);
        RegistrarVistaDemo(disponible: false);

        var cut = Render<VistasTecnicas>();

        cut.Markup.Should().NotContain("vistas-chip");
        cut.FindAll("form").Should().BeEmpty();
    }

    [Fact]
    public void El_Administrador_ve_un_chip_con_la_vista_previa_de_vocabulario_en_su_panel()
    {
        Autenticar(Roles.Administrador);
        RegistrarVistaDemo(disponible: false);

        var cut = Render<VistasTecnicas>();

        var chip = cut.Find("button.vistas-chip");
        chip.GetAttribute("aria-expanded").Should().Be("false");
        chip.TextContent.Should().Contain("Vistas");
        cut.Find("#panel-vistas-tecnicas select.selector-vista-vocabulario").Should().NotBeNull();
        cut.FindAll("form").Should().ContainSingle(f => f.GetAttribute("action") == "/cuenta/vista-vocabulario");
    }

    [Fact]
    public void El_chip_muestra_la_vista_de_demo_activa_y_abre_y_cierra_su_panel()
    {
        Autenticar(Roles.Consulta);
        RegistrarVistaDemo(disponible: true, new VistaDemoEfectiva(VistaDemo.GestorCae, Marta));

        var cut = Render<VistasTecnicas>();

        cut.Find("[data-testid=vistas-chip-etiqueta]").TextContent.Should().Be("Demostración · Gestor CAE");
        cut.FindAll("form").Should().ContainSingle(f => f.GetAttribute("action") == "/cuenta/vista-demo");
        cut.Find("#panel-vistas-tecnicas").ClassList.Should().NotContain("abierto");

        cut.Find("button.vistas-chip").Click();
        cut.Find("button.vistas-chip").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("#panel-vistas-tecnicas").ClassList.Should().Contain("abierto");

        cut.Find(".panel-desplegable")
            .KeyDown(new KeyboardEventArgs { Key = "Escape" });
        cut.Find("button.vistas-chip").GetAttribute("aria-expanded").Should().Be("false", "Esc cierra el panel");
    }

    [Fact]
    public void El_velo_de_fuera_cierra_el_panel_del_chip()
    {
        Autenticar(Roles.Consulta);
        RegistrarVistaDemo(disponible: true);

        var cut = Render<VistasTecnicas>();
        cut.Find("button.vistas-chip").Click();

        cut.Find(".panel-desplegable-velo").Click();

        cut.Find("button.vistas-chip").GetAttribute("aria-expanded").Should().Be("false");
        cut.FindAll(".panel-desplegable-velo").Should().BeEmpty();
    }

    /// <summary>
    /// Decisión de producto del 2026-10-08: quien eligió un avatar del catálogo se ve con él en
    /// lugar de sus iniciales, y desde el menú de cuenta se llega a cambiarlo pulsándolo.
    /// </summary>
    [Fact]
    public void El_menu_de_usuario_pinta_el_avatar_elegido_y_enlaza_a_cambiarlo()
    {
        Services.AddSingleton<IOptions<OpcionesLocalizacion>>(
            Opciones.Create(new OpcionesLocalizacion { CatalanHabilitado = true }));
        Services.AddSingleton<IMediator>(new MediatorFalso(request => request switch
        {
            ObtenerAvatarPropioQuery => "zorro-verde",
            _ => throw new NotSupportedException(request.GetType().Name),
        }));

        var cut = Render<MenuUsuario>(p => p.Add(m => m.Nombre, "Elena Ríos"));

        var boton = cut.Find("#menu-cuenta-boton");
        boton.TextContent.Trim().Should().Be("🦊");
        boton.QuerySelector(".avatar-usuario")!.ClassList.Should().Contain("avatar-usuario-tono-verde");
        boton.GetAttribute("aria-label").Should().Be("Menú de usuario", "un emoji tampoco es nombre accesible");

        var cambiar = cut.Find("#menu-cuenta-panel a[href='/mi-avatar']");
        cambiar.GetAttribute("role").Should().Be("menuitem");
        cambiar.QuerySelector(".avatar-usuario")!.TextContent.Trim().Should().Be("🦊");
        cambiar.TextContent.Should().Contain("Elena Ríos").And.Contain("Cambiar mi avatar");
    }

    /// <summary>
    /// El avatar es decoración: si su lectura falla, la cabecera de todas las páginas se
    /// pinta con las iniciales en vez de caerse.
    /// </summary>
    [Fact]
    public void Si_no_se_puede_leer_el_avatar_el_menu_de_usuario_pinta_las_iniciales()
    {
        Services.AddSingleton<IOptions<OpcionesLocalizacion>>(
            Opciones.Create(new OpcionesLocalizacion { CatalanHabilitado = true }));
        Services.AddSingleton<IMediator>(new MediatorFalso(_ => throw new InvalidOperationException("base caída")));

        var cut = Render<MenuUsuario>(p => p.Add(m => m.Nombre, "Elena Ríos"));

        cut.Find("#menu-cuenta-boton").TextContent.Trim().Should().Be("ER");
    }

    // ---- Migas (propuesta 03) ----

    private IRenderedComponent<MigasPagina> RenderMigas(string ruta, bool primeraPersona = false)
    {
        Services.AddSingleton<IMediator>(new MediatorFalso(request => request switch
        {
            UsaRotulosPrimeraPersonaQuery => primeraPersona,
            _ => throw new NotSupportedException(request.GetType().Name),
        }));
        Services.GetRequiredService<NavigationManager>().NavigateTo(ruta);
        return Render<MigasPagina>();
    }

    [Fact]
    public void Una_seccion_del_menu_muestra_su_grupo_y_su_nombre_como_pagina_actual()
    {
        var cut = RenderMigas("/empresas");

        var migas = cut.FindAll("li.miga");
        migas.Select(m => m.TextContent.Trim()).Should().Equal("Negocio", "Empresas");
        migas[^1].GetAttribute("aria-current").Should().Be("page");
        cut.Find("nav.migas").GetAttribute("aria-label").Should().Be("Ruta de la página");
    }

    [Fact]
    public void Una_ficha_sube_a_su_seccion_con_un_enlace()
    {
        var id = Guid.NewGuid();

        var cut = RenderMigas($"/centros/{id}");

        cut.Find("li.miga a").GetAttribute("href").Should().Be("/centros");
        cut.FindAll("li.miga").Select(m => m.TextContent.Trim()).Should().Equal("Negocio", "Centros", "Ficha");
    }

    [Fact]
    public void Una_subpantalla_que_no_es_una_ficha_no_inventa_un_nombre()
    {
        var cut = RenderMigas("/documentos/revision-ia");

        cut.FindAll("li.miga").Select(m => m.TextContent.Trim()).Should().Equal("Negocio", "Documentos");
    }

    [Fact]
    public void La_miga_dice_lo_mismo_que_el_menu_en_primera_persona()
    {
        var cut = RenderMigas("/empresas", primeraPersona: true);

        cut.FindAll("li.miga").Select(m => m.TextContent.Trim()).Should().Equal("Negocio", "Mi empresa");
    }

    [Fact]
    public void Una_ruta_que_no_es_del_menu_no_lleva_migas_y_navegar_las_actualiza()
    {
        var cut = RenderMigas("/mi-firma");
        cut.Markup.Should().NotContain("migas");

        Services.GetRequiredService<NavigationManager>().NavigateTo("/visitas");

        cut.WaitForAssertion(() =>
            cut.FindAll("li.miga").Select(m => m.TextContent.Trim()).Should().Equal("Operación", "Visitas"));
    }

    [Fact]
    public void Mi_trabajo_tiene_miga_tanto_con_un_Tenant_como_con_varios()
    {
        RenderMigas("/mi-trabajo").FindAll("li.miga").Select(m => m.TextContent.Trim())
            .Should().Equal("Operación", "Mi trabajo");
    }

    // ---- Campana de avisos (propuesta 04) ----

    private static NotificacionDto Notificacion(string titulo, string? url = null) =>
        new(Guid.NewGuid(), titulo, "detalle de " + titulo, url, null);

    private static AvisoRevisionNormativaDto Normativa(bool revisado) =>
        new(Guid.NewGuid(), "BOE-A-2026-1", new DateOnly(2026, 9, 7), "Real Decreto de prueba",
            "https://www.boe.es/", "RD 1627/1997", revisado, revisado ? DateTime.UtcNow : null);

    private static SolicitudIncorporacionCarteraDto Solicitud(bool puedeResolver) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Empresa Uno", Guid.NewGuid(), "Marta", "Por favor",
            CaeManager.Domain.Operaciones.EstadoSolicitudIncorporacionCartera.Pendiente, DateTime.UtcNow, null, null,
            puedeResolver, PuedeRevocar: false);

    private MediatorFalso RegistrarCampana(
        IReadOnlyList<NotificacionDto>? notificaciones = null,
        IReadOnlyList<AvisoRevisionNormativaDto>? normativa = null,
        bool esCoordinadorCae = false,
        IReadOnlyList<SolicitudIncorporacionCarteraDto>? solicitudes = null,
        AlertaDePrincipal? alertaSinPrincipal = null)
    {
        var mediador = new MediatorFalso(request => request switch
        {
            ObtenerOperacionesSinPrincipalQuery => alertaSinPrincipal ?? AlertaDePrincipal.Ninguna,
            ObtenerNotificacionesPendientesQuery => notificaciones ?? [],
            ObtenerAvisosRevisionNormativaQuery => normativa ?? [],
            ObtenerSolicitudesIncorporacionCarteraQuery => Result.Exito(
                new BandejaIncorporacionCarteraDto(esCoordinadorCae, solicitudes ?? [], [], [])),
            MarcarNotificacionLeidaCommand => Result.Exito(),
            _ => throw new NotSupportedException(request.GetType().Name),
        });
        Services.AddSingleton<IMediator>(mediador);
        return mediador;
    }

    [Fact]
    public void Sin_avisos_la_campana_no_lleva_contador_y_el_panel_lo_dice()
    {
        RegistrarCampana();

        var cut = Render<CampanaAvisos>();

        cut.FindAll("[data-testid=campana-contador]").Should().BeEmpty();
        cut.Find("button.campana-boton").GetAttribute("aria-label").Should().Be("Avisos pendientes");
        cut.Find(".campana-vacio").TextContent.Should().Contain("No tienes avisos pendientes");
    }

    [Fact]
    public void El_contador_suma_las_tres_fuentes_y_solo_cuenta_lo_pendiente_y_resoluble()
    {
        RegistrarCampana(
            notificaciones: [Notificacion("Aptitud médica vencida"), Notificacion("Visita mañana")],
            normativa: [Normativa(revisado: false), Normativa(revisado: true)],
            esCoordinadorCae: true,
            solicitudes: [Solicitud(puedeResolver: true), Solicitud(puedeResolver: true), Solicitud(puedeResolver: false)]);

        var cut = Render<CampanaAvisos>();

        // 2 notificaciones + 1 normativa sin revisar + 1 aviso de cartera (agrupa las 2 resolubles).
        cut.Find("[data-testid=campana-contador]").TextContent.Should().Be("4");
        cut.Find("button.campana-boton").GetAttribute("aria-label").Should().Be("Avisos pendientes, 4 sin leer");
        cut.FindAll(".campana-item").Should().HaveCount(4);
        cut.Markup.Should().Contain("2 solicitudes pendientes");
    }

    private static OperacionEnAlertaDePrincipal EnAlerta(string nombre, SituacionDePrincipal situacion) =>
        new(Guid.NewGuid(), Guid.NewGuid(), nombre, situacion,
            situacion == SituacionDePrincipal.SinNadieAsignado ? 0 : 1,
            situacion == SituacionDePrincipal.CoordinadorCaePrincipal ? "Carla" : null, EsDeQuienConsulta: false);

    [Fact]
    public void Las_empresas_sin_principal_son_un_solo_elemento_resumen_que_lleva_a_la_bandeja_de_cartera()
    {
        RegistrarCampana(alertaSinPrincipal: new AlertaDePrincipal(true,
        [
            EnAlerta("Talleres Norte", SituacionDePrincipal.SinNadieAsignado),
            EnAlerta("Obras Sur", SituacionDePrincipal.ConPersonasSinPrincipal),
            EnAlerta("Con coordinador", SituacionDePrincipal.CoordinadorCaePrincipal),
        ]));

        var cut = Render<CampanaAvisos>();

        cut.Find("[data-testid=campana-contador]").TextContent.Should().Be("1");
        var item = cut.FindAll(".campana-item").Should().ContainSingle().Subject;
        item.TextContent.Should().Contain("Empresas sin Gestor CAE principal")
            .And.Contain("2 empresas de tu organización no tienen principal",
                "la que tiene un Coordinador CAE principal es informativa y no cuenta");
        item.GetAttribute("href").Should().Be("/cartera/solicitudes");
    }

    [Fact]
    public void Con_todas_las_empresas_con_principal_la_campana_no_avisa()
    {
        RegistrarCampana(alertaSinPrincipal: new AlertaDePrincipal(true,
            [EnAlerta("Con coordinador", SituacionDePrincipal.CoordinadorCaePrincipal)]));

        Render<CampanaAvisos>().FindAll(".campana-item").Should().BeEmpty();
    }

    [Fact]
    public void Quien_no_es_Coordinador_CAE_no_ve_avisos_de_cartera_aunque_la_query_devuelva_pendientes()
    {
        RegistrarCampana(esCoordinadorCae: false, solicitudes: [Solicitud(puedeResolver: true)]);

        var cut = Render<CampanaAvisos>();

        cut.FindAll("[data-testid=campana-contador]").Should().BeEmpty(
            "el rol lo decide la Query; la campana no amplía lo que ve cada rol");
    }

    [Fact]
    public void Una_notificacion_se_marca_leida_al_abrirla_y_baja_el_contador()
    {
        var notificacion = Notificacion("Aptitud médica vencida");
        var mediador = RegistrarCampana(notificaciones: [notificacion]);
        var cut = Render<CampanaAvisos>();
        cut.Find("[data-testid=campana-contador]").TextContent.Should().Be("1");

        cut.Find("button.campana-item").Click();

        mediador.Enviadas.OfType<MarcarNotificacionLeidaCommand>().Should().ContainSingle(c => c.Id == notificacion.Id);
        cut.FindAll("[data-testid=campana-contador]").Should().BeEmpty();
    }

    [Fact]
    public void Marcar_las_notificaciones_leidas_no_toca_las_normativas_ni_las_de_cartera()
    {
        var mediador = RegistrarCampana(
            notificaciones: [Notificacion("Una"), Notificacion("Otra")],
            normativa: [Normativa(revisado: false)]);
        var cut = Render<CampanaAvisos>();

        cut.Find("button.campana-marcar").Click();

        mediador.Enviadas.OfType<MarcarNotificacionLeidaCommand>().Should().HaveCount(2);
        cut.Find("[data-testid=campana-contador]").TextContent.Should().Be("1", "queda la normativa sin revisar");
        cut.FindAll("button.campana-marcar").Should().BeEmpty("ya no quedan notificaciones que marcar");
    }

    [Fact]
    public void Abrir_el_panel_vuelve_a_leer_las_fuentes()
    {
        var mediador = RegistrarCampana();
        var cut = Render<CampanaAvisos>();
        var lecturasAlMontar = mediador.Enviadas.OfType<ObtenerNotificacionesPendientesQuery>().Count();

        cut.Find("button.campana-boton").Click();

        mediador.Enviadas.OfType<ObtenerNotificacionesPendientesQuery>().Count().Should().Be(lecturasAlMontar + 1);
        cut.Find("button.campana-boton").GetAttribute("aria-expanded").Should().Be("true");
    }

    // ---- Banda de avisos del sistema (propuesta 07) ----

    [Fact]
    public void El_aviso_de_fin_de_acceso_es_un_aviso_de_la_banda_y_el_unico_que_se_puede_descartar()
    {
        var contexto = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        contexto.Items[AvisoFinDeAcceso.ClaveItems] = MotivoFinDeAcceso.VentanaDeSoporte;
        Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(
            new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = contexto });

        var cut = Render<AvisoFinDeAccesoEstatico>();

        var aviso = cut.Find(".aviso-fin-de-acceso");
        aviso.HasAttribute("data-aviso-sistema").Should().BeTrue();
        aviso.GetAttribute("data-tono").Should().Be("soporte");
        aviso.TextContent.Should().Contain(AvisoFinDeAcceso.Texto(MotivoFinDeAcceso.VentanaDeSoporte));
        aviso.QuerySelector("button[data-descartar-aviso]")!.TextContent.Trim().Should().Be("Descartar");
    }

    [Fact]
    public void Sin_motivo_no_hay_aviso_que_descartar()
    {
        Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(
            new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() });

        Render<AvisoFinDeAccesoEstatico>().Markup.Trim().Should().BeEmpty();
    }

    // ---- Dobles ----

    private sealed class VistaDemoFalsa(bool disponible, VistaDemoEfectiva? efectiva) : IVistaDemoActual
    {
        public Task<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default) => Task.FromResult(disponible);

        public Task<VistaDemoEfectiva?> ObtenerEfectivaAsync(CancellationToken cancellationToken = default) => Task.FromResult(efectiva);

        public Task<IReadOnlyList<GestorDeVistaDemo>> ObtenerGestoresElegiblesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GestorDeVistaDemo>>([new GestorDeVistaDemo(Marta, "Marta")]);

        public Task<IReadOnlyList<Guid>?> ObtenerTenantIdsAcotadosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>?>(null);
    }

    private sealed class VistaPreviaFalsa : IVistaVocabularioPreviewService
    {
        public PerfilVocabularioTenant? PerfilForzado => null;
    }

    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => null;
    }

    private sealed class MediatorFalso(Func<object, object?> responder) : IMediator
    {
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return Task.FromResult((TResponse)responder(request)!);
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
