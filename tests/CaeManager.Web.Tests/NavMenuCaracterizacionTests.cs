using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Plataforma.OrdenMenu;
using CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
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
    ];

    public sealed record Combinacion(
        string Rol, VistaDemo? Vista, bool Comunicaciones, bool AdminPlataforma,
        PerfilVocabularioTenant Perfil, int Tenants)
    {
        public override string ToString() =>
            $"{Rol}|vista={(Vista?.ToString() ?? "-")}|com={(Comunicaciones ? 1 : 0)}|plat={(AdminPlataforma ? 1 : 0)}" +
            $"|perfil={Perfil}|tenants={Tenants}";
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
        administrador.Should().Contain("dashboards(open)<nav-grupo-detalle|Dashboards>[\"\"@dashboard/icono icono-medio'Dashboard'<nav-item>")
            .And.Contain("plataforma(open)<nav-grupo-detalle|Plataforma>[")
            .And.Contain("\"configuracion\"@configuracion/icono icono-medio'Configuración'")
            .And.NotContain("@?");

        var cliente = Describir(
            new Combinacion(Roles.Cliente, null, true, false, PerfilVocabularioTenant.Consultora, 1), iconos);
        cliente.Should().StartWith("suelto[\"\"@dashboard/icono icono-medio'Dashboard'<nav-item>");
    }

    /// <summary>
    /// En el cajón móvil (<c>NavegacionMovil</c>, <c>InteractiveServer</c>) el menú vive en un
    /// circuito y la sesión puede pasar a anónima a mitad (revalidación de la cookie). La
    /// <c>AuthorizeView</c> del marcado anterior reaccionaba al nuevo estado en cascada y ocultaba
    /// los grupos al momento; el catálogo tiene que hacer lo mismo en vez de quedarse con el
    /// usuario con el que se inicializó.
    /// </summary>
    [Fact]
    public void Si_la_sesion_pasa_a_anonima_en_un_circuito_el_menu_oculta_los_grupos()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
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

    private static IRenderedComponent<NavMenu> Pintar(Combinacion c, OrdenMenuLateralDto? orden)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
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
            if (el.LocalName == "details")
            {
                if (enSuelto) { sb.Append("] "); enSuelto = false; }
                var titulo = el.QuerySelector("summary > span")?.TextContent.Trim();
                sb.Append($"{el.GetAttribute("data-grupo")}{(el.HasAttribute("open") ? "(open)" : "")}" +
                          $"<{el.GetAttribute("class")}|{titulo}>[");
                foreach (var a in el.QuerySelectorAll("a"))
                    sb.Append(Enlace(a, iconos)).Append("; ");
                sb.Append("] ");
            }
            else if (el.LocalName == "a" && el.Closest("details") is null)
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
                EsAdministradorPlataformaQuery => c.AdminPlataforma,
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
