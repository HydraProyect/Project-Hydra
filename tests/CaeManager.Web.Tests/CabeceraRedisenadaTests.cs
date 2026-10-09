using CaeManager.Application.Usuarios.Queries.ObtenerAvatarPropio;
using System.Security.Claims;
using Bunit;
using CaeManager.Application.Notificaciones.Commands.MarcarNotificacionLeida;
using CaeManager.Application.Notificaciones.Queries.ObtenerNotificacionesPendientes;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Application.Operaciones.IncorporacionCartera.Commands;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;
using CaeManager.Application.VigilanciaNormativa.Commands.MarcarAvisoRevisionNormativaRevisado;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Queries.UsaRotulosPrimeraPersona;
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
        enlaces.Should().Equal(
            ["/mi-avatar", "/mi-firma", "/cuenta/configurar-2fa", "/cuenta/cambiar-contrasena?motivo=mi-cuenta"],
            "el propio avatar, arriba del todo, lleva a elegirlo; después «Mi cuenta»: firma, 2FA y contraseña");

        panel.QuerySelector("form.selector-idioma-formulario")!.GetAttribute("action").Should().Be("/cuenta/idioma",
            "el idioma sigue siendo el <form> POST de siempre, ahora dentro del menú");

        var salida = panel.QuerySelectorAll("form").Single(f => f.GetAttribute("action") == "/cuenta/cerrar-sesion");
        salida.GetAttribute("method").Should().Be("post");
        var cerrar = salida.QuerySelector("button.boton-cerrar-sesion")!;
        cerrar.GetAttribute("role").Should().Be("menuitem");
        cerrar.TextContent.Trim().Should().Be("Cerrar sesión");
    }

    /// <summary>
    /// FS-16: leer o escribir credenciales de plataforma exige 2FA a roles a los que no se
    /// les fuerza al iniciar sesión (AutorizacionSecretosDeTenantBehavior), y la entrada
    /// lateral «Verificación en dos pasos» solo la ve Administrador. El menú de usuario es
    /// la puerta voluntaria de todos, así que no depende del rol: se pinta sin autorización
    /// registrada. Las dos páginas son estáticas y con otro layout.
    /// </summary>
    [Fact]
    public void El_menu_de_usuario_ofrece_Mi_cuenta_con_dos_pasos_y_contrasena_a_cualquier_rol()
    {
        Services.AddSingleton<IOptions<OpcionesLocalizacion>>(Opciones.Create(new OpcionesLocalizacion()));
        Services.AddSingleton<IMediator>(new MediatorFalso(_ => null));

        var cut = Render<MenuUsuario>(p => p.Add(m => m.Nombre, "Marta Ruiz"));

        var panel = cut.Find("#menu-cuenta-panel");
        panel.QuerySelector(".menu-cuenta-titulo")!.TextContent.Trim().Should().Be("Mi cuenta");

        var dosPasos = panel.QuerySelector("a[href='/cuenta/configurar-2fa']")!;
        dosPasos.TextContent.Trim().Should().Be("Verificación en dos pasos");
        dosPasos.GetAttribute("role").Should().Be("menuitem");
        dosPasos.GetAttribute("data-enhance-nav").Should().Be("false");

        var contrasena = panel.QuerySelector("a[href='/cuenta/cambiar-contrasena?motivo=mi-cuenta']")!;
        contrasena.TextContent.Trim().Should().Be("Cambiar contraseña");
        contrasena.GetAttribute("role").Should().Be("menuitem");
        contrasena.GetAttribute("data-enhance-nav").Should().Be("false");
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
        bool esActorPlataforma = false,
        Result? resultadoDeComandos = null,
        Error? errorDeBandeja = null)
    {
        var mediador = new MediatorFalso(request => request switch
        {
            ObtenerNotificacionesPendientesQuery => notificaciones ?? [],
            ObtenerAvisosRevisionNormativaQuery => normativa ?? [],
            EsAdministradorPlataformaQuery => esActorPlataforma,
            ObtenerSolicitudesIncorporacionCarteraQuery when errorDeBandeja is not null =>
                Result.Fallo<BandejaIncorporacionCarteraDto>(errorDeBandeja),
            ObtenerSolicitudesIncorporacionCarteraQuery => Result.Exito(
                new BandejaIncorporacionCarteraDto(esCoordinadorCae, solicitudes ?? [], [], [])),
            MarcarNotificacionLeidaCommand => Result.Exito(),
            MarcarAvisoRevisionNormativaRevisadoCommand or AceptarSolicitudIncorporacionCarteraCommand
                or RechazarSolicitudIncorporacionCarteraCommand => resultadoDeComandos ?? Result.Exito(),
            _ => throw new NotSupportedException(request.GetType().Name),
        });
        Services.AddSingleton<IMediator>(mediador);
        Services.AddSingleton<ToastService>();
        return mediador;
    }

    private static SolicitudIncorporacionCarteraDto SolicitudDe(string solicitante, string empresa, bool puedeResolver = true) =>
        new(Guid.NewGuid(), Guid.NewGuid(), empresa, Guid.NewGuid(), solicitante, "Por favor",
            CaeManager.Domain.Operaciones.EstadoSolicitudIncorporacionCartera.Pendiente, DateTime.UtcNow, null, null,
            puedeResolver, PuedeRevocar: false);

    private ToastService Toasts => Services.GetRequiredService<ToastService>();

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

        // 2 notificaciones + 1 normativa sin revisar + 2 solicitudes resolubles (una entrada por solicitud).
        cut.Find("[data-testid=campana-contador]").TextContent.Should().Be("5");
        cut.Find("button.campana-boton").GetAttribute("aria-label").Should().Be("Avisos pendientes, 5 sin leer");
        cut.FindAll(".campana-item").Should().HaveCount(5);
    }

    [Fact]
    public void La_notificacion_muestra_su_texto_de_accion_o_el_de_por_defecto_si_trae_destino()
    {
        RegistrarCampana(notificaciones:
        [
            new(Guid.NewGuid(), "Con texto", "m", "/documentos", "Ver documento"),
            new(Guid.NewGuid(), "Sin texto", "m", "/documentos", null),
            new(Guid.NewGuid(), "Sin destino", "m", null, "Ignorado"),
        ]);

        var cut = Render<CampanaAvisos>();

        cut.FindAll(".campana-nuevo").Select(e => e.TextContent.Trim())
            .Should().Equal("Ver documento", "Gestionar", "Nuevo");
    }

    [Fact]
    public void Quien_no_es_Actor_de_Plataforma_ve_el_aviso_normativo_sin_boton_de_marcar_revisado()
    {
        RegistrarCampana(normativa: [Normativa(revisado: false)], esActorPlataforma: false);

        var cut = Render<CampanaAvisos>();

        cut.FindAll("[data-aviso^=normativa]").Should().ContainSingle("control positivo: el aviso se ve");
        cut.FindAll("[data-aviso^=normativa] button").Should().BeEmpty();
    }

    [Fact]
    public void El_Actor_de_Plataforma_marca_revisado_y_se_envia_el_Command_de_ese_aviso()
    {
        var aviso = Normativa(revisado: false);
        var mediador = RegistrarCampana(normativa: [aviso], esActorPlataforma: true);
        var cut = Render<CampanaAvisos>();

        cut.Find("[data-aviso^=normativa] button").Click();

        mediador.Enviadas.OfType<MarcarAvisoRevisionNormativaRevisadoCommand>().Should().ContainSingle()
            .Which.Should().Be(new MarcarAvisoRevisionNormativaRevisadoCommand(aviso.Id, null));
        mediador.Enviadas.OfType<ObtenerAvisosRevisionNormativaQuery>().Count().Should().Be(2, "recarga tras marcar");
    }

    [Fact]
    public void Sin_avisos_normativos_pendientes_no_se_pregunta_si_es_Actor_de_Plataforma()
    {
        var mediador = RegistrarCampana(normativa: [Normativa(revisado: true)]);

        Render<CampanaAvisos>();

        mediador.Enviadas.OfType<EsAdministradorPlataformaQuery>().Should().BeEmpty();
    }

    [Fact]
    public void Aceptar_envia_el_Command_de_esa_solicitud_avisa_y_recarga()
    {
        var solicitud = SolicitudDe("Marta", "Empresa Norte");
        var mediador = RegistrarCampana(esCoordinadorCae: true, solicitudes: [solicitud]);
        var cut = Render<CampanaAvisos>();

        cut.Find($"[data-aviso='cartera:{solicitud.Id}'] button").Click();

        mediador.Enviadas.OfType<AceptarSolicitudIncorporacionCarteraCommand>().Should().ContainSingle()
            .Which.Should().Be(new AceptarSolicitudIncorporacionCarteraCommand(solicitud.Id));
        Toasts.Mensajes.Should().ContainSingle(t => t.Tono == TonoToast.Exito && t.Mensaje.Contains("Marta"));
        mediador.Enviadas.OfType<ObtenerSolicitudesIncorporacionCarteraQuery>().Count().Should().Be(2);
    }

    [Fact]
    public void Rechazar_pide_confirmacion_y_cancelar_no_envia_el_Command()
    {
        var solicitud = SolicitudDe("Marta", "Empresa Norte");
        var mediador = RegistrarCampana(esCoordinadorCae: true, solicitudes: [solicitud]);
        var cut = Render<CampanaAvisos>();

        cut.FindAll($"[data-aviso='cartera:{solicitud.Id}'] button").Single(b => b.TextContent.Trim() == "Rechazar").Click();

        mediador.Enviadas.OfType<RechazarSolicitudIncorporacionCarteraCommand>().Should().BeEmpty("se confirma antes de enviar");
        cut.FindAll(".modal-pie button").Select(b => b.TextContent.Trim()).Should().Contain("Rechazar", "control positivo: el diálogo se abrió");

        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Cancelar").Click();

        mediador.Enviadas.OfType<RechazarSolicitudIncorporacionCarteraCommand>().Should().BeEmpty();
        cut.FindAll(".modal-pie").Should().BeEmpty();
    }

    [Fact]
    public void Rechazar_y_confirmar_envia_el_Command_de_esa_solicitud()
    {
        var solicitud = SolicitudDe("Marta", "Empresa Norte");
        var mediador = RegistrarCampana(esCoordinadorCae: true, solicitudes: [solicitud]);
        var cut = Render<CampanaAvisos>();

        cut.FindAll($"[data-aviso='cartera:{solicitud.Id}'] button").Single(b => b.TextContent.Trim() == "Rechazar").Click();
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Rechazar").Click();

        mediador.Enviadas.OfType<RechazarSolicitudIncorporacionCarteraCommand>().Should().ContainSingle()
            .Which.Should().Be(new RechazarSolicitudIncorporacionCarteraCommand(solicitud.Id));
        Toasts.Mensajes.Should().ContainSingle(t => t.Tono == TonoToast.Exito);
    }

    [Fact]
    public void Si_otro_Coordinador_CAE_ya_la_resolvio_se_explica_con_un_toast_de_error()
    {
        var solicitud = SolicitudDe("Marta", "Empresa Norte");
        RegistrarCampana(esCoordinadorCae: true, solicitudes: [solicitud],
            resultadoDeComandos: Result.Fallo(ErroresSolicitudCartera.YaResuelta));
        var cut = Render<CampanaAvisos>();

        cut.Find($"[data-aviso='cartera:{solicitud.Id}'] button").Click();

        Toasts.Mensajes.Should().ContainSingle(t => t.Tono == TonoToast.Error);
    }

    [Fact]
    public void Una_notificacion_con_destino_navega_a_el_y_una_sin_destino_no_navega()
    {
        var conDestino = new NotificacionDto(Guid.NewGuid(), "Con destino", "m", "/documentos", null);
        var sinDestino = new NotificacionDto(Guid.NewGuid(), "Sin destino", "m", null, null);
        RegistrarCampana(notificaciones: [conDestino, sinDestino]);
        var cut = Render<CampanaAvisos>();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        var inicio = navegacion.Uri;

        cut.FindAll("button.campana-item").Single(b => b.TextContent.Contains("Sin destino")).Click();
        navegacion.Uri.Should().Be(inicio, "sin UrlAccion solo se marca leída");

        cut.FindAll("button.campana-item").Single(b => b.TextContent.Contains("Con destino")).Click();
        navegacion.Uri.Should().EndWith("/documentos");
    }

    [Fact]
    public void Si_la_Query_de_solicitudes_niega_el_permiso_la_campana_se_pinta_sin_avisos_de_cartera()
    {
        RegistrarCampana(notificaciones: [Notificacion("Una")], errorDeBandeja: ErroresSolicitudCartera.SinPermiso);

        var cut = Render<CampanaAvisos>();

        cut.Find("[data-testid=campana-contador]").TextContent.Should().Be("1");
        cut.FindAll("[data-aviso^=cartera]").Should().BeEmpty();
    }

    [Fact]
    public void Si_el_Command_de_marcar_revisado_falla_se_avisa_con_un_toast_de_error()
    {
        RegistrarCampana(normativa: [Normativa(revisado: false)], esActorPlataforma: true,
            resultadoDeComandos: Result.Fallo(ErroresSolicitudCartera.SinPermiso));
        var cut = Render<CampanaAvisos>();

        cut.Find("[data-aviso^=normativa] button").Click();

        Toasts.Mensajes.Should().ContainSingle(t => t.Tono == TonoToast.Error);
    }

    [Fact]
    public void El_aviso_normativo_muestra_la_norma_y_la_fecha_de_publicacion()
    {
        RegistrarCampana(normativa: [Normativa(revisado: false)]);

        var cut = Render<CampanaAvisos>();

        cut.Find("[data-aviso^=normativa] small").TextContent.Should().Be("RD 1627/1997 · 07/09/2026");
    }

    [Fact]
    public void Tras_aceptar_la_solicitud_resuelta_desaparece_de_la_campana()
    {
        var solicitud = SolicitudDe("Marta", "Empresa Norte");
        var pendientes = new List<SolicitudIncorporacionCarteraDto> { solicitud };
        var mediador = new MediatorFalso(request =>
        {
            switch (request)
            {
                case ObtenerNotificacionesPendientesQuery: return (IReadOnlyList<NotificacionDto>)[];
                case ObtenerAvisosRevisionNormativaQuery: return (IReadOnlyList<AvisoRevisionNormativaDto>)[];
                case ObtenerSolicitudesIncorporacionCarteraQuery:
                    return Result.Exito(new BandejaIncorporacionCarteraDto(true, pendientes.ToList(), [], []));
                case AceptarSolicitudIncorporacionCarteraCommand:
                    pendientes.Clear();
                    return Result.Exito();
                default: throw new NotSupportedException(request.GetType().Name);
            }
        });
        Services.AddSingleton<IMediator>(mediador);
        Services.AddSingleton<ToastService>();
        var cut = Render<CampanaAvisos>();
        cut.FindAll("[data-aviso^=cartera]").Should().ContainSingle("control positivo");

        cut.Find($"[data-aviso='cartera:{solicitud.Id}'] button").Click();

        cut.FindAll("[data-aviso^=cartera]").Should().BeEmpty();
        cut.FindAll("[data-testid=campana-contador]").Should().BeEmpty();
    }

    [Fact]
    public void El_toast_de_la_resolucion_sale_aunque_falle_la_recarga_posterior()
    {
        var solicitud = SolicitudDe("Marta", "Empresa Norte");
        var resuelta = false;
        var mediador = new MediatorFalso(request =>
        {
            switch (request)
            {
                case ObtenerNotificacionesPendientesQuery when resuelta: throw new InvalidOperationException("recarga rota");
                case ObtenerNotificacionesPendientesQuery: return (IReadOnlyList<NotificacionDto>)[];
                case ObtenerAvisosRevisionNormativaQuery: return (IReadOnlyList<AvisoRevisionNormativaDto>)[];
                case ObtenerSolicitudesIncorporacionCarteraQuery:
                    return Result.Exito(new BandejaIncorporacionCarteraDto(true, [solicitud], [], []));
                case AceptarSolicitudIncorporacionCarteraCommand:
                    resuelta = true;
                    return Result.Exito();
                default: throw new NotSupportedException(request.GetType().Name);
            }
        });
        Services.AddSingleton<IMediator>(mediador);
        Services.AddSingleton<ToastService>();
        Services.AddSingleton<CaeManager.Application.Common.IAlertaOperativa>(new AlertaMuda());
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        var cut = Render<CampanaAvisos>();

        cut.Find($"[data-aviso='cartera:{solicitud.Id}'] button").Click();

        Toasts.Mensajes.Should().ContainSingle(t => t.Tono == TonoToast.Exito, "la solicitud ya se aceptó antes de la recarga rota");
    }

    private sealed class AlertaMuda : CaeManager.Application.Common.IAlertaOperativa
    {
        public void Emitir(string mensaje, CaeManager.Application.Common.NivelAlertaOperativa nivel) { }
        public void CapturarExcepcion(Exception excepcion) { }
        public void DejarMigaDePan(string mensaje) { }
        public IDisposable IniciarAmbitoDeCaptura() => new Nada();
        private sealed class Nada : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public void La_cabecera_sube_su_apilamiento_mientras_tiene_un_dialogo_dentro()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        var css = File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Components", "Layout", "MainLayout.razor.css"));

        css.Should().Contain(".cabecera-fija:has(.modal-superposicion)",
            "la confirmación de Rechazar vive dentro del contexto de apilamiento de la cabecera");
    }

    [Fact]
    public void El_layout_monta_solo_la_campana_y_ninguno_de_los_tres_avisos_retirados()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull();
        var layout = File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Components", "Layout", "MainLayout.razor"));

        layout.Should().Contain("Notificaciones.CampanaAvisos", "control positivo: la campana sigue montada");
        layout.Should().NotContain("NotificacionesPopup").And.NotContain("PanelAvisosNormativos").And.NotContain("AvisoSolicitudesCartera");
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
