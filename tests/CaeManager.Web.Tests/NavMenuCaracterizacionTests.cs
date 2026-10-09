using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Plataforma.OrdenMenu;
using CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Tenants.Queries.UsaRotulosPrimeraPersona;
using CaeManager.Application.VistaDemo;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// Caracterización del menú lateral (<see cref="NavMenu"/>): para cada rol, lente de demo
/// (<see cref="VistaDemo"/>), función activable (Comunicaciones) y capacidad de plataforma,
/// fija QUÉ enlaces ve el usuario, EN QUÉ ORDEN, bajo qué grupo, con qué ruta, icono, rótulo y
/// coincidencia de ruta, y qué grupos nacen abiertos. La referencia
/// (<see cref="NavMenuCaracterizacionEsperado"/>) se capturó del marcado escrito a mano antes de
/// convertir el menú en catálogo; el catálogo tiene que reproducirla línea a línea.
///
/// <para>
/// La coincidencia de ruta (<c>NavLinkMatch.All</c> frente a prefijo) no deja rastro en el
/// marcado salvo por la clase <c>active</c>, así que el menú se pinta con la navegación en
/// <c>/empresas</c>: un Dashboard (<c>href=""</c>) con prefijo se marcaría activo en todas las
/// páginas. Los iconos se identifican renderizando <see cref="Icono"/> con cada nombre conocido
/// y comparando el SVG; uno irreconocible se escribe como <c>?</c> y rompe la comparación.
/// </para>
/// </summary>
public class NavMenuCaracterizacionTests
{
    private static readonly string[] Roles_ =
    [
        Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae, Roles.Consulta,
        Roles.Cliente, "(sin rol)",
    ];

    private static readonly VistaDemo?[] Vistas = [null, VistaDemo.Direccion, VistaDemo.CoordinadorCae, VistaDemo.GestorCae];

    private static readonly string[] NombresIcono =
    [
        "dashboard", "clientes", "empresas", "centros", "subcontratas", "trabajadores", "vehiculos", "documentos",
        "asignaciones", "proyectos", "visitas", "alertas", "calendario", "reportes", "configuracion", "usuarios",
        "seguridad", "chat", "cartera", "evaluaciones", "incidencias", "chevron", "plataforma",
        "inicio", "ejecutivo", "solicitud", "mi-trabajo", "facturacion", "conectar", "delegacion", "etiqueta", "equipo",
    ];

    public sealed record Combinacion(
        string Rol, VistaDemo? Vista, bool Comunicaciones, bool AdminPlataforma,
        PerfilVocabularioTenant Perfil, int Tenants, bool OtroTenant = false)
    {
        public override string ToString() =>
            $"{Rol}|vista={(Vista?.ToString() ?? "-")}|com={(Comunicaciones ? 1 : 0)}|plat={(AdminPlataforma ? 1 : 0)}" +
            $"|perfil={Perfil}|tenants={Tenants}" + (OtroTenant ? "|otroTenant=1" : "");
    }

    /// <summary>
    /// Producto cruzado completo de rol × lente × Comunicaciones × capacidad de plataforma, más
    /// las dos dimensiones que solo cambian un rótulo o una ruta (perfil de vocabulario y número
    /// de Tenants autorizados) probadas para cada rol con el resto por defecto.
    /// </summary>
    public static IEnumerable<Combinacion> Combinaciones()
    {
        foreach (var rol in Roles_)
            foreach (var vista in Vistas)
                foreach (var com in new[] { false, true })
                    foreach (var plat in new[] { false, true })
                        yield return new Combinacion(rol, vista, com, plat, PerfilVocabularioTenant.Consultora, 1);

        foreach (var rol in Roles_)
        {
            yield return new Combinacion(rol, null, true, false, PerfilVocabularioTenant.ClienteDirecto, 1);
            yield return new Combinacion(rol, null, true, false, PerfilVocabularioTenant.Consultora, 2);
        }
    }

    [Fact]
    public void Cada_combinacion_ve_los_mismos_enlaces_en_el_mismo_orden_que_el_menu_escrito_a_mano()
    {
        var iconos = CatalogoDeIconos();
        var real = string.Join('\n', Combinaciones().Select(c => $"{c} => {(Describir(c, iconos) is { Length: > 0 } d ? d : "(ningún enlace)")}"));

        var esperado = NavMenuCaracterizacionEsperado.Texto.Replace("\r\n", "\n").Trim();
        if (real != esperado)
        {
            var ruta = Path.Combine(Path.GetTempPath(), "navmenu-caracterizacion.real.txt");
            File.WriteAllText(ruta, real);
            var diferencias = real.Split('\n').Zip(esperado.Split('\n'))
                .Where(p => p.First != p.Second)
                .Take(3)
                .Select(p => $"real:     {p.First}\nesperado: {p.Second}");
            Assert.Fail($"El menú cambió respecto de la caracterización (salida completa en {ruta}). " +
                        $"Líneas reales {real.Split('\n').Length}, esperadas {esperado.Split('\n').Length}.\n" +
                        string.Join("\n\n", diferencias));
        }
    }

    /// <summary>
    /// Control positivo del propio instrumento: si la extracción no viera los enlaces (selector
    /// equivocado, iconos sin reconocer), la comparación podría casar dos salidas vacías.
    /// </summary>
    [Fact]
    public void El_instrumento_ve_enlaces_e_iconos_reales()
    {
        var iconos = CatalogoDeIconos();
        iconos.Should().HaveCount(NombresIcono.Length, "cada icono del menú tiene un SVG distinto");

        var administrador = Describir(
            new Combinacion(Roles.Administrador, null, true, true, PerfilVocabularioTenant.Consultora, 1), iconos);
        administrador.Should().Contain("dashboards(open)<nav-grupo-detalle|Dashboards>[\"\"@inicio/icono icono-medio'Inicio'<nav-item>")
            .And.Contain("plataforma(open)<nav-grupo-detalle|Plataforma>[")
            .And.Contain("\"configuracion\"@configuracion/icono icono-medio'Configuración'")
            .And.NotContain("@?");

        var cliente = Describir(
            new Combinacion(Roles.Cliente, null, true, false, PerfilVocabularioTenant.Consultora, 1), iconos);
        cliente.Should().StartWith("suelto[\"\"@dashboard/icono icono-medio'Inicio'<nav-item>");
    }

    /// <summary>
    /// En el cajón móvil (<c>NavegacionMovil</c>, <c>InteractiveServer</c>) el menú vive en un
    /// circuito y la sesión puede pasar a anónima a mitad (revalidación de la cookie). La
    /// <c>AuthorizeView</c> del marcado anterior reaccionaba al nuevo estado en cascada y ocultaba
    /// los grupos al momento; el catálogo tiene que hacer lo mismo en vez de quedarse con el
    /// usuario con el que se inicializó.
    /// </summary>
    /// <summary>
    /// DDL-072, decisión del propietario 2026-09-28: el rótulo en primera persona exige perfil Cliente
    /// Directo Y usuario del propio Tenant propietario. Un Coordinador CAE externo dentro de un
    /// Tenant beneficiario Cliente Directo ve «Empresas» y «Trabajadores».
    /// </summary>
    [Theory]
    [InlineData(PerfilVocabularioTenant.ClienteDirecto, false, "Mi empresa", "Mis trabajadores")]
    [InlineData(PerfilVocabularioTenant.ClienteDirecto, true, "Empresas", "Trabajadores")]
    [InlineData(PerfilVocabularioTenant.Consultora, false, "Empresas", "Trabajadores")]
    public void Los_rotulos_en_primera_persona_exigen_perfil_ClienteDirecto_y_usuario_del_Tenant_propietario(
        PerfilVocabularioTenant perfil, bool otroTenant, string empresas, string trabajadores)
    {
        var cut = Pintar(new Combinacion(Roles.Administrador, null, true, false, perfil, 1, otroTenant), orden: null);

        string Rotulo(string href) => cut.Find($"nav.nav-principal a[href='{href}']").TextContent.Trim();
        Rotulo("empresas").Should().Be(empresas);
        Rotulo("trabajadores").Should().Be(trabajadores);
    }

    [Fact]
    public void Si_la_sesion_pasa_a_anonima_en_un_circuito_el_menu_oculta_los_grupos()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddLocalization();
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("usuario@prueba").SetRoles(Roles.Administrador);
        ctx.Services.AddSingleton<IOptions<ComunicacionesOptions>>(
            Options.Create(new ComunicacionesOptions { Activo = true }));
        ctx.Services.AddSingleton<IMediator>(new MediatorDeMenu(
            new Combinacion(Roles.Administrador, null, true, true, PerfilVocabularioTenant.Consultora, 1)));

        var cut = ctx.Render<NavMenu>();
        cut.FindAll("details[data-grupo]").Should().NotBeEmpty("con la sesión abierta el Administrador ve sus grupos");

        auth.SetNotAuthorized();

        cut.WaitForAssertion(() => cut.FindAll("nav.nav-principal a").Select(a => a.GetAttribute("href")).Should().BeEmpty(
            "sin sesión no queda ningún enlace, igual que con la AuthorizeView anterior"));
    }

    /// <summary>
    /// Orden global guardado (decisión del 2026-09-23): recoloca grupos y enlaces, deja al final
    /// lo que no nombra, ignora identificadores que ya no existen y nunca muestra lo que el rol
    /// no permitía ver.
    /// </summary>
    [Fact]
    public void El_orden_guardado_recoloca_grupos_y_enlaces_sin_ampliar_lo_visible()
    {
        var orden = new OrdenMenuLateralDto(
            ["plataforma", "grupo-retirado", "control"],
            ["conectores-cae", "enlace-retirado", "estado-comercial"],
            Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow);

        var administrador = Pintar(
            new Combinacion(Roles.Administrador, null, true, true, PerfilVocabularioTenant.Consultora, 1), orden);
        administrador.FindAll("details[data-grupo]").Select(d => d.GetAttribute("data-grupo")).Should().Equal(
            ["plataforma", "control", "dashboards", "negocio", "operacion", "administracion"],
            "los guardados primero y en su orden; los que el orden no nombra, al final en el orden del catálogo; " +
            "'grupo-retirado' no existe y se ignora");
        administrador.FindAll("details[data-grupo='plataforma'] a").Select(a => a.GetAttribute("href")).Should().Equal(
            ["plataforma/conectores-cae", "configuracion/comercial", "delegaciones"],
            "dentro del grupo manda la posición relativa de la lista plana de enlaces");

        var gestor = Pintar(
            new Combinacion(Roles.GestorCae, null, true, false, PerfilVocabularioTenant.Consultora, 1), orden);
        gestor.FindAll("details[data-grupo]").Select(d => d.GetAttribute("data-grupo")).Should()
            .NotContain(["plataforma", "administracion"], "el orden no es autoridad: no enseña grupos que el rol no ve")
            .And.StartWith("control");
    }

    /// <summary>
    /// La fila solo se valida al escribir: un nulo o un repetido metidos por SQL no pueden tumbar
    /// el menú de todos los Tenants.
    /// </summary>
    [Fact]
    public void La_reconciliacion_tolera_nulos_y_repetidos_de_una_fila_tocada_a_mano()
    {
        string[] catalogo = ["a", "b", "c"];

        CatalogoMenuLateral.Reconciliar(catalogo, x => x, ["c", null!, "c", "fantasma", "a"])
            .Should().Equal("c", "a", "b");
    }

    /// <summary>
    /// Mejora de la barra lateral (2026-10-04): la navegación se anuncia con nombre, cada cabecera
    /// declara si está expandida, y un grupo con un solo enlace visible va plano, sin cabecera.
    /// </summary>
    [Fact]
    public void La_barra_se_anuncia_y_los_grupos_con_cabecera_declaran_su_estado()
    {
        var cut = Pintar(new Combinacion(Roles.Administrador, null, true, true, PerfilVocabularioTenant.Consultora, 1), null);

        cut.Find("nav.nav-principal").GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace();
        var cabeceras = cut.FindAll("details[data-grupo] > summary");
        cabeceras.Should().NotBeEmpty();
        foreach (var summary in cabeceras)
        {
            var detalle = summary.ParentElement!;
            summary.GetAttribute("aria-expanded").Should().Be(detalle.HasAttribute("open") ? "true" : "false");
            detalle.GetAttribute("data-abierto-defecto").Should().Be(detalle.HasAttribute("open") ? "true" : "false");
        }
    }

    [Fact]
    public void Un_grupo_con_un_solo_enlace_visible_se_pinta_plano_sin_cabecera()
    {
        // Dirección CAE ve en Administración solo «Usuarios» (Configuración y 2FA son del Administrador).
        var direccion = Pintar(new Combinacion(Roles.DireccionCae, null, true, false, PerfilVocabularioTenant.Consultora, 1), null);
        var plano = direccion.Find("[data-grupo='administracion']");

        plano.LocalName.Should().Be("div");
        plano.ClassList.Should().Contain("nav-grupo-plano");
        plano.QuerySelector("summary").Should().BeNull();
        plano.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).Should().Equal("usuarios");

        // Con tres enlaces (Administrador) sí lleva cabecera.
        var administrador = Pintar(new Combinacion(Roles.Administrador, null, true, false, PerfilVocabularioTenant.Consultora, 1), null);
        administrador.Find("[data-grupo='administracion']").LocalName.Should().Be("details");
    }

    /// <summary>
    /// Los fijados son una preferencia del navegador, nunca un permiso: cada estrella lleva el
    /// identificador de un enlace que YA se pinta en este menú, así que lo que se puede fijar nunca
    /// supera lo que el rol ya ve. Un Gestor CAE no tiene estrella para «configuracion».
    /// </summary>
    [Fact]
    public void Solo_se_puede_fijar_lo_que_el_rol_ya_ve_en_su_menu()
    {
        var gestor = Pintar(new Combinacion(Roles.GestorCae, null, true, false, PerfilVocabularioTenant.Consultora, 1), null);
        var fijables = gestor.FindAll("[data-fijar]").Select(b => b.GetAttribute("data-fijar")).ToList();
        var filas = gestor.FindAll(".nav-fila").Select(f => f.GetAttribute("data-enlace")).ToList();

        fijables.Should().NotBeEmpty().And.Equal(filas);
        fijables.Should().NotContain(["configuracion", "delegaciones", "usuarios"]);
        fijables.Should().OnlyContain(id => CatalogoMenuLateral.Enlaces.Any(e => e.Id == id));
        gestor.FindAll(".nav-contador, .nav-insignia").Should().BeEmpty(
            "no hay contadores: ninguna de sus fuentes existe como Query de recuento (ver NavMenu.razor)");
    }

    /// <summary>
    /// Barra lateral, ajuste del 2026-10-08: el filtro deja de ser un campo siempre visible con atajo «/»
    /// y pasa a ser solo una lupa en la cabecera que despliega el campo. Sin <c>kbd</c>, y el campo
    /// recogido va <c>inert</c> (ni foco por teclado ni lectura de pantalla).
    /// </summary>
    [Fact]
    public void El_filtro_es_una_lupa_que_despliega_el_campo_sin_insignia_de_atajo()
    {
        var cut = Pintar(new Combinacion(Roles.Administrador, null, true, false, PerfilVocabularioTenant.Consultora, 1), null);

        var lupa = cut.Find("button[data-menu-lupa]");
        lupa.GetAttribute("aria-expanded").Should().Be("false");
        lupa.GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace();

        var campo = cut.Find("input[data-menu-filtro]");
        campo.HasAttribute("inert").Should().BeTrue("recogido, el campo no puede recibir foco");
        campo.Closest("[data-menu-busqueda]")!.QuerySelector("[data-menu-lupa]").Should().NotBeNull(
            "lupa y campo son un solo elemento que se expande en la misma fila: no hay fila nueva");
        cut.Find("[data-menu-busqueda]").HasAttribute("data-abierta").Should().BeFalse("nace recogida");
        cut.FindAll("kbd, .nav-filtro-tecla").Should().BeEmpty("el atajo «/» y su insignia se retiraron");
    }

    /// <summary>
    /// La lupa encuentra opciones de dentro de las páginas, pero solo las que el rol ya ve: la lista sale
    /// del catálogo filtrada con la visibilidad de su enlace padre y su propia condición.
    /// </summary>
    [Theory]
    [InlineData(Roles.Administrador, true, true)]
    [InlineData(Roles.DireccionCae, false, true)]
    [InlineData(Roles.GestorCae, false, true)]
    [InlineData(Roles.Consulta, false, false)]
    public void La_lupa_solo_ofrece_subopciones_que_el_rol_ya_puede_ver(string rol, bool configuracion, bool gestionDocumental)
    {
        var cut = Pintar(new Combinacion(rol, null, true, false, PerfilVocabularioTenant.Consultora, 1), null);
        var ids = cut.FindAll("[data-subopcion]").Select(a => a.GetAttribute("data-subopcion")!).ToList();

        ids.Any(i => i.StartsWith("config-")).Should().Be(configuracion, "Configuración solo existe para el Administrador");
        ids.Contains("documentos-plantillas").Should().Be(gestionDocumental);
        ids.Contains("documentos-plataformas").Should().Be(gestionDocumental);
        ids.Should().Contain("documentos-preventivo", "esa pestaña no tiene restricción propia: la ve todo el que ve Documentos");

        // Cada subopción cuelga de un enlace del menú que ESTE rol ve en su barra.
        var enlacesVisibles = cut.FindAll(".nav-fila").Select(f => f.GetAttribute("data-enlace")).ToHashSet();
        foreach (var id in ids)
            enlacesVisibles.Should().Contain(CatalogoMenuLateral.Subopciones.Single(s => s.Id == id).EnlaceId);
    }

    /// <summary>Macros.razor.cs expulsa a /not-found sin Comunicaciones:Activo: la lupa no puede ofrecerla.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void La_subopcion_Macros_sigue_a_Comunicaciones_activo(bool comunicaciones, bool esperada)
    {
        var cut = Pintar(new Combinacion(Roles.Administrador, null, comunicaciones, false, PerfilVocabularioTenant.Consultora, 1), null);

        cut.FindAll("[data-subopcion='config-macros']").Any().Should().Be(esperada);
        cut.FindAll("[data-subopcion='config-usuarios']").Should().HaveCount(1, "control positivo: el resto de Configuración sigue");
    }

    [Fact]
    public void Una_subopcion_navega_a_su_ruta_directa_con_el_rotulo_y_el_padre()
    {
        var cut = Pintar(new Combinacion(Roles.Administrador, null, true, false, PerfilVocabularioTenant.Consultora, 1), null);

        var plantillas = cut.Find("a[data-subopcion='documentos-plantillas']");
        plantillas.GetAttribute("href").Should().Be("documentos?pestana=plantillas");
        plantillas.QuerySelector(".nav-resultado-texto")!.TextContent.Trim().Should().Be("Plantillas");
        plantillas.QuerySelector(".nav-resultado-padre")!.TextContent.Trim().Should().Be("Documentos");

        var usuarios = cut.Find("a[data-subopcion='config-usuarios']");
        usuarios.GetAttribute("href").Should().Be("configuracion/usuarios");
        usuarios.QuerySelector(".nav-resultado-padre")!.TextContent.Trim().Should().Be("Configuración");
        usuarios.QuerySelector(".nav-resultado-texto")!.TextContent.Trim().Should().NotBeEmpty();
        usuarios.HasAttribute("hidden").Should().BeTrue("nacen ocultas: el JS muestra solo las que casan con lo escrito");
    }

    [Fact]
    public void Las_subopciones_del_catalogo_tienen_padre_ids_unicos_y_rutas_relativas()
    {
        var subs = CatalogoMenuLateral.Subopciones;

        subs.Select(s => s.Id).Should().OnlyHaveUniqueItems();
        subs.Select(s => s.EnlaceId).Distinct().Should().OnlyContain(id => CatalogoMenuLateral.Enlaces.Any(e => e.Id == id));
        subs.Should().OnlyContain(s => !s.Ruta.StartsWith('/') && s.Ruta.StartsWith(
            CatalogoMenuLateral.Enlaces.Single(e => e.Id == s.EnlaceId).Ruta),
            "la subopción vive bajo la ruta de su enlace padre: es otra vista de la misma página");
        subs.Should().OnlyContain(s => s.ClaveRotulo.Length > 0);
    }

    /// <summary>
    /// Las pestañas de gestión documental se pintan a todos pero su contenido es solo de estos roles; el
    /// catálogo duplica la lista (la constante de la página es privada), así que se vigila que coincidan.
    /// </summary>
    [Fact]
    public void Los_roles_de_gestion_documental_del_catalogo_son_los_de_la_pagina_de_Documentos()
    {
        const BindingFlags privado = BindingFlags.NonPublic | BindingFlags.Static;
        var deLaPagina = typeof(CaeManager.Web.Features.Documentos.Pages.Documentos)
            .GetField("RolesDeGestionDocumental", privado)!.GetRawConstantValue();
        var delCatalogo = typeof(CatalogoMenuLateral).GetField("RolesDeGestionDocumental", privado)!.GetRawConstantValue();

        delCatalogo.Should().NotBeNull().And.Be(deLaPagina);
    }

    [Fact]
    public void La_ruta_activa_se_anuncia_con_aria_current_y_ninguna_otra()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddLocalization();
        ctx.AddAuthorization().SetAuthorized("usuario@prueba").SetRoles(Roles.Administrador);
        ctx.Services.AddSingleton<IOptions<ComunicacionesOptions>>(
            Options.Create(new ComunicacionesOptions { Activo = true }));
        ctx.Services.AddSingleton<IMediator>(new MediatorDeMenu(
            new Combinacion(Roles.Administrador, null, true, false, PerfilVocabularioTenant.Consultora, 1)));
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("empresas");

        var cut = ctx.Render<NavMenu>();

        cut.FindAll("a[aria-current='page']").Select(a => a.GetAttribute("href")).Should().Equal("empresas");
        cut.FindAll("a.active").Should().HaveCount(1);
    }

    /// <summary>
    /// Defecto C1 del piloto Outbound (2026-10-08): el menú ofrecía «Mi trabajo» al rol Consulta y sus dos
    /// páginas (<c>/bandeja</c> con un Tenant autorizado, <c>/mi-trabajo</c> con varios) se lo negaban. El
    /// menú no ofrece un destino que la página niega: se compara con el <c>[Authorize]</c> de la página a
    /// la que lleva el enlace en cada caso, no con una lista escrita aquí.
    /// </summary>
    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.CoordinadorCae)]
    [InlineData(Roles.GestorCae)]
    [InlineData(Roles.Consulta)]
    public void El_menu_ofrece_Mi_trabajo_solo_a_quien_su_pagina_autoriza(string rol)
    {
        foreach (var tenants in new[] { 1, 2 })
        {
            var pagina = tenants > 1
                ? typeof(CaeManager.Web.Features.Bandeja.Pages.MiTrabajo)
                : typeof(CaeManager.Web.Features.Bandeja.Pages.Bandeja);
            var autorizados = pagina.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()!.Roles!
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            autorizados.Should().NotBeEmpty("control: la página restringe por rol; si dejara de hacerlo, este test no mediría nada");

            var cut = Pintar(new Combinacion(rol, null, false, false, PerfilVocabularioTenant.Consultora, tenants), null);
            var ofrecido = cut.FindAll(".nav-fila").Any(f => f.GetAttribute("data-enlace") == "mi-trabajo");

            ofrecido.Should().Be(autorizados.Contains(rol), $"rol {rol} con {tenants} Tenant(s) autorizado(s): la página es {pagina.Name}");
        }
    }

    private static IRenderedComponent<NavMenu> Pintar(Combinacion c, OrdenMenuLateralDto? orden)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddLocalization();
        ctx.AddAuthorization().SetAuthorized("usuario@prueba").SetRoles(c.Rol);
        ctx.Services.AddSingleton<IOptions<ComunicacionesOptions>>(
            Options.Create(new ComunicacionesOptions { Activo = c.Comunicaciones }));
        ctx.Services.AddSingleton<IMediator>(new MediatorDeMenu(c, orden));
        return ctx.Render<NavMenu>();
    }

    private static Dictionary<string, string> CatalogoDeIconos()
    {
        using var ctx = new BunitContext();
        return NombresIcono.ToDictionary(
            nombre => HuellaSvg(ctx.Render<Icono>(p => p.Add(i => i.Nombre, nombre)).Find("svg")),
            nombre => nombre);
    }

    private static string HuellaSvg(IElement svg) => Regex.Replace(svg.InnerHtml, @"\s+", "");

    private static string Describir(Combinacion c, Dictionary<string, string> iconos)
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddLocalization();
        var auth = ctx.AddAuthorization();
        if (c.Rol != "(sin rol)")
            auth.SetAuthorized("usuario@prueba").SetRoles(c.Rol);
        else
            auth.SetAuthorized("usuario@prueba");

        ctx.Services.AddSingleton<IOptions<ComunicacionesOptions>>(
            Options.Create(new ComunicacionesOptions { Activo = c.Comunicaciones }));
        ctx.Services.AddSingleton<IMediator>(new MediatorDeMenu(c));
        if (c.Vista is { } vista)
            ctx.Services.AddSingleton<IVistaDemoActual>(new VistaDemoFija(vista));

        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("empresas");
        var cut = ctx.Render<NavMenu>();

        var sb = new StringBuilder();
        var nav = cut.Find("nav.nav-principal");
        var enSuelto = false;
        foreach (var el in nav.Descendants<IElement>())
        {
            var esGrupo = el.LocalName == "details"
                          || (el.LocalName == "div" && el.ClassList.Contains("nav-grupo-detalle"));
            if (esGrupo)
            {
                if (enSuelto) { sb.Append("] "); enSuelto = false; }
                // Un grupo con un solo enlace se pinta plano: sin cabecera y sin «open».
                var titulo = el.QuerySelector("summary > span")?.TextContent.Trim();
                sb.Append($"{el.GetAttribute("data-grupo")}{(el.HasAttribute("open") ? "(open)" : "")}" +
                          $"<{el.GetAttribute("class")}|{titulo}>[");
                foreach (var a in el.QuerySelectorAll("a"))
                    sb.Append(Enlace(a, iconos)).Append("; ");
                sb.Append("] ");
            }
            else if (el.LocalName == "a" && el.Closest(".nav-grupo-detalle") is null && el.Closest("[data-menu-fijados]") is null
                     // Los resultados de la lupa no son enlaces del menú: los cubren los tests de búsqueda.
                     && el.Closest("[data-menu-resultados]") is null)
            {
                if (!enSuelto) { sb.Append("suelto["); enSuelto = true; }
                sb.Append(Enlace(el, iconos)).Append("; ");
            }
        }
        if (enSuelto) sb.Append("] ");
        return sb.ToString().TrimEnd();
    }

    private static string Enlace(IElement a, Dictionary<string, string> iconos)
    {
        var svg = a.QuerySelector("svg");
        var icono = svg is null ? "-" : iconos.GetValueOrDefault(HuellaSvg(svg), "?") + "/" + svg.GetAttribute("class");
        var texto = Regex.Replace(a.TextContent, @"\s+", " ").Trim();
        var clase = a.GetAttribute("class");
        return $"\"{a.GetAttribute("href")}\"@{icono}'{texto}'<{clase}>";
    }

    private sealed class MediatorDeMenu(Combinacion c, OrdenMenuLateralDto? orden = null) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(request switch
            {
                ObtenerPerfilVocabularioActualQuery => (object)c.Perfil,
                UsaRotulosPrimeraPersonaQuery => c.Perfil == PerfilVocabularioTenant.ClienteDirecto && !c.OtroTenant,
                EsAdministradorPlataformaQuery => c.AdminPlataforma,
                // Sin Workspace operativo derivado, el rol de origen es el del claim.
                ParticipaEnIncorporacionCarteraQuery => c.Rol is Roles.CoordinadorCae or Roles.GestorCae,
                ObtenerClientesAutorizadosQuery => Enumerable.Range(0, c.Tenants)
                    .Select(i => new ClienteAutorizadoDto(Guid.NewGuid(), $"Tenant {i}", i == 0))
                    .ToList() as IReadOnlyList<ClienteAutorizadoDto>,
                ObtenerOrdenMenuLateralQuery => orden,
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            })!);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => throw new NotSupportedException();
    }

    private sealed class VistaDemoFija(VistaDemo vista) : IVistaDemoActual
    {
        public Task<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<VistaDemoEfectiva?> ObtenerEfectivaAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<VistaDemoEfectiva?>(new VistaDemoEfectiva(vista, null));
        public Task<IReadOnlyList<GestorDeVistaDemo>> ObtenerGestoresElegiblesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GestorDeVistaDemo>>([]);
        public Task<IReadOnlyList<Guid>?> ObtenerTenantIdsAcotadosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>?>(null);
    }
}
