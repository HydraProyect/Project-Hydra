using System.Reflection;
using System.Security.Claims;
using Bunit;
using CaeManager.Application.VistaDemo;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.AtajosGlobales;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pestañas de sección (entrega de Design del 2026-10-17): qué pestañas ve cada rol, cuál es la activa,
/// que los roles del catálogo son los de la pantalla de destino y que cada destino existe. Una pestaña
/// solo señala: la pantalla de destino se autoriza por su cuenta, así que el catálogo nunca puede ofrecer
/// algo que la pantalla niega ni esconder algo que la pantalla permite sin que lo sepamos.
/// </summary>
public class CatalogoSeccionesMenuTests
{
    private static ContextoMenuLateral Contexto(
        string rol, VistaDemo? vista = null, bool porEncargo = false, bool pleno = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, rol) };
        if (porEncargo)
            claims.Add(new Claim(RolEfectivoDelWorkspaceMiddleware.TipoClaimEncargoAdministracion, "encargo"));
        if (porEncargo && pleno)
            claims.Add(new Claim(RolEfectivoDelWorkspaceMiddleware.TipoClaimEncargoPleno, "encargo"));
        return new ContextoMenuLateral(
            new ClaimsPrincipal(new ClaimsIdentity(claims, "prueba")), vista, true, false,
            PerfilVocabularioTenant.Consultora, false);
    }

    private static string[] Ids(SeccionVisible? seccion) =>
        seccion?.Pestanas.Select(p => p.Pestana.Id).ToArray() ?? [];

    private static string? Activa(SeccionVisible? seccion) =>
        seccion?.Pestanas.SingleOrDefault(p => p.Activa)?.Pestana.Id;

    [Theory]
    [InlineData(Roles.Administrador, new[] { "documentos-todos", "documentos-revision-ia", "documentos-incidencias" })]
    [InlineData(Roles.DireccionCae, new[] { "documentos-todos", "documentos-revision-ia", "documentos-incidencias" })]
    [InlineData(Roles.CoordinadorCae, new[] { "documentos-todos", "documentos-revision-ia", "documentos-incidencias" })]
    [InlineData(Roles.GestorCae, new[] { "documentos-todos", "documentos-revision-ia", "documentos-incidencias" })]
    [InlineData(Roles.Consulta, new[] { "documentos-todos", "documentos-incidencias" })]
    public void Documentos_ofrece_a_cada_rol_solo_las_pestanas_cuya_pantalla_le_abre(string rol, string[] esperadas)
    {
        Ids(CatalogoSeccionesMenu.Resolver("documentos", null, Contexto(rol))).Should().Equal(esperadas);
    }

    [Fact]
    public void El_rol_Cliente_ve_Documentos_sin_barra_porque_le_queda_una_sola_pestana()
    {
        CatalogoSeccionesMenu.Resolver("documentos", null, Contexto(Roles.Cliente)).Should().BeNull();
    }

    [Fact]
    public void Una_ruta_que_no_es_de_ninguna_seccion_no_pinta_barra()
    {
        CatalogoSeccionesMenu.Resolver("empresas", null, Contexto(Roles.Administrador)).Should().BeNull();
        CatalogoSeccionesMenu.Resolver("", null, Contexto(Roles.Administrador)).Should().BeNull();
    }

    [Theory]
    [InlineData("documentos", null, "documentos-todos")]
    [InlineData("documentos", "plantillas", "documentos-todos")]
    [InlineData("documentos", "revision-ia", "documentos-revision-ia")]
    [InlineData("DOCUMENTOS", "REVISION-IA", "documentos-revision-ia")]
    [InlineData("documentos/revision-ia", null, "documentos-revision-ia")]
    [InlineData("incidencias", null, "documentos-incidencias")]
    [InlineData("incidencias/11111111-2222-3333-4444-555555555555", null, "documentos-incidencias")]
    public void La_pestana_activa_sale_de_la_ruta_y_del_parametro_pestana(string ruta, string? pestana, string activa)
    {
        var seccion = CatalogoSeccionesMenu.Resolver(ruta, pestana, Contexto(Roles.Administrador));

        Activa(seccion).Should().Be(activa);
        seccion!.Pestanas.Count(p => p.Activa).Should().Be(1, "nunca dos pestañas activas a la vez");
    }

    [Fact]
    public void Equipo_ofrece_Usuarios_y_Roles_al_Administrador_y_marca_la_actual()
    {
        var enUsuarios = CatalogoSeccionesMenu.Resolver("usuarios", null, Contexto(Roles.Administrador));
        var enRoles = CatalogoSeccionesMenu.Resolver("roles", null, Contexto(Roles.Administrador));

        Ids(enUsuarios).Should().Equal("equipo-usuarios", "equipo-roles");
        Activa(enUsuarios).Should().Be("equipo-usuarios");
        Ids(enRoles).Should().Equal("equipo-usuarios", "equipo-roles");
        Activa(enRoles).Should().Be("equipo-roles");
    }

    [Theory]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.CoordinadorCae)]
    public void Sin_el_rol_Administrador_Equipo_no_pinta_barra_porque_Roles_no_se_le_abre(string rol)
    {
        CatalogoSeccionesMenu.Resolver("usuarios", null, Contexto(rol)).Should().BeNull(
            "queda solo Usuarios: una barra de una pestaña no lleva a ninguna parte");
    }

    [Fact]
    public void Quien_administra_por_encargo_no_pleno_no_ve_la_pestana_Roles_y_con_encargo_pleno_si()
    {
        // Roles es una página que el Encargo de administración no abre (D-8): la pestaña no se ofrece.
        CatalogoSeccionesMenu.Resolver("usuarios", null, Contexto(Roles.Administrador, porEncargo: true))
            .Should().BeNull();

        Ids(CatalogoSeccionesMenu.Resolver("usuarios", null, Contexto(Roles.Administrador, porEncargo: true, pleno: true)))
            .Should().Equal("equipo-usuarios", "equipo-roles");
    }

    [Theory]
    [InlineData(VistaDemo.GestorCae)]
    [InlineData(VistaDemo.CoordinadorCae)]
    public void La_lente_de_demo_solo_puede_ocultar_pestanas_nunca_mostrar_una_que_el_rol_real_no_tiene(VistaDemo vista)
    {
        // Administrador mirando con la lente de Gestor o de Coordinador: Roles (solo Administrador) desaparece.
        CatalogoSeccionesMenu.Resolver("usuarios", null, Contexto(Roles.Administrador, vista)).Should().BeNull();

        // Un Gestor CAE con la lente de Coordinador no gana Usuarios: la lente nunca amplía el rol real.
        CatalogoSeccionesMenu.Resolver("usuarios", null, Contexto(Roles.GestorCae, VistaDemo.CoordinadorCae)).Should().BeNull();
    }

    [Fact]
    public void Con_la_lente_de_Gestor_Documentos_conserva_sus_tres_pestanas()
    {
        Ids(CatalogoSeccionesMenu.Resolver("documentos", null, Contexto(Roles.GestorCae, VistaDemo.GestorCae)))
            .Should().Equal("documentos-todos", "documentos-revision-ia", "documentos-incidencias");
    }

    // --- Los roles del catálogo son los de la pantalla de destino -----------------------------------

    private static string[] RolesDe(Type pagina) =>
        (pagina.GetCustomAttribute<AuthorizeAttribute>()?.Roles ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(r => r, StringComparer.Ordinal).ToArray();

    private static string[] RolesDe(string roles) =>
        roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(r => r, StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("documentos-incidencias", typeof(Features.Incidencias.Pages.Incidencias))]
    [InlineData("documentos-revision-ia", typeof(Features.Documentos.Pages.RevisionIa))]
    [InlineData("equipo-usuarios", typeof(Features.Usuarios.Pages.Usuarios))]
    [InlineData("equipo-roles", typeof(Features.GestionRoles.Pages.Roles))]
    public void Los_roles_de_cada_pestana_son_exactamente_los_del_Authorize_de_su_pantalla(string id, Type pagina)
    {
        var pestana = CatalogoSeccionesMenu.Secciones.SelectMany(s => s.Pestanas).Single(p => p.Id == id);

        RolesDe(pestana.Roles).Should().Equal(RolesDe(pagina),
            "la pestaña no puede ofrecer lo que la pantalla niega ni esconder lo que permite");
    }

    [Fact]
    public void Todos_los_documentos_es_la_pantalla_Documentos_abierta_a_todo_usuario_autenticado()
    {
        typeof(Features.Documentos.Pages.Documentos).GetCustomAttribute<AuthorizeAttribute>()!.Roles
            .Should().BeNullOrEmpty("Documentos se autoriza sin roles: la pestaña la ven todos los roles con barra");
    }

    [Fact]
    public void Cada_pestana_apunta_a_una_pantalla_que_existe()
    {
        var rutasReales = typeof(CatalogoSeccionesMenu).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
            .Select(r => r.Template.Trim('/').ToLowerInvariant())
            .ToHashSet();

        foreach (var pestana in CatalogoSeccionesMenu.Secciones.SelectMany(s => s.Pestanas))
            rutasReales.Should().Contain(pestana.Ruta.Split('?')[0].Trim('/').ToLowerInvariant(),
                $"la pestaña {pestana.Id} no puede llevar a una pantalla inexistente");
    }

    [Fact]
    public void El_atajo_U_lleva_a_una_pantalla_de_subida_que_existe()
    {
        var rutasReales = typeof(CatalogoSeccionesMenu).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
            .Select(r => r.Template)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        rutasReales.Should().Contain(CatalogoAtajos.RutaSubida);
    }

    [Fact]
    public void La_ruta_de_cada_pestana_pertenece_a_una_entrada_del_menu_que_existe()
    {
        foreach (var seccion in CatalogoSeccionesMenu.Secciones)
        {
            CatalogoMenuLateral.Enlaces.Should().Contain(e => e.Id == seccion.EnlaceId,
                $"la sección {seccion.Id} cuelga de una entrada del menú lateral");
            seccion.Pestanas.Select(p => p.Id).Should().OnlyHaveUniqueItems();
        }
    }

    [Fact]
    public void Incidencias_Revision_IA_Usuarios_y_Roles_no_son_ya_entradas_sueltas_del_menu_salvo_la_de_Equipo()
    {
        var ids = CatalogoMenuLateral.Enlaces.Select(e => e.Id).ToList();

        ids.Should().NotContain("incidencias");
        ids.Should().NotContain("roles");
        CatalogoMenuLateral.Subopciones.Select(s => s.Id).Should().NotContain(["config-usuarios", "config-roles"]);
    }
}

/// <summary>La barra de 48 px bajo la cabecera: lo que pinta y cómo sigue la navegación del circuito.</summary>
public class BarraSeccionTests : BunitContext
{
    public BarraSeccionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ExcepcionDeCircuitoDesconectado>>(
            NullLogger<ExcepcionDeCircuitoDesconectado>.Instance);
    }

    private IRenderedComponent<BarraSeccion> Pintar(string url, params string[] roles)
    {
        var identidad = new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)), "prueba");
        var estado = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identidad)));
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        return Render<BarraSeccion>(p => p.AddCascadingValue(estado));
    }

    [Fact]
    public void En_Documentos_el_Administrador_ve_las_tres_pestanas_en_orden_con_la_primera_activa()
    {
        var cut = Pintar("documentos", Roles.Administrador);

        var barra = cut.Find("nav[data-testid=barra-seccion]");
        barra.GetAttribute("data-seccion").Should().Be("documentos");
        barra.GetAttribute("aria-label").Should().Be("Secciones de Documentos");
        var enlaces = cut.FindAll("a.barra-seccion-enlace");
        enlaces.Select(a => a.TextContent.Trim()).Should().Equal("Todos los documentos", "Revisión IA", "Incidencias");
        enlaces.Select(a => a.GetAttribute("href")).Should().Equal(
            "documentos", "documentos?pestana=revision-ia", "incidencias");
        enlaces.Select(a => a.GetAttribute("aria-current")).Should().Equal("page", null, null);
    }

    [Fact]
    public void El_parametro_pestana_marca_Revision_IA_como_actual()
    {
        var cut = Pintar("documentos?pestana=revision-ia", Roles.GestorCae);

        cut.Find("a[data-pestana=documentos-revision-ia]").GetAttribute("aria-current").Should().Be("page");
        cut.Find("a[data-pestana=documentos-todos]").HasAttribute("aria-current").Should().BeFalse();
    }

    [Fact]
    public void Consulta_no_ve_la_pestana_Revision_IA()
    {
        var cut = Pintar("incidencias", Roles.Consulta);

        cut.FindAll("a.barra-seccion-enlace").Select(a => a.GetAttribute("data-pestana"))
            .Should().Equal("documentos-todos", "documentos-incidencias");
        cut.Find("a[data-pestana=documentos-incidencias]").GetAttribute("aria-current").Should().Be("page");
    }

    [Fact]
    public void En_Equipo_el_Administrador_ve_Usuarios_y_Roles()
    {
        var cut = Pintar("roles", Roles.Administrador);

        cut.Find("nav.barra-seccion").GetAttribute("aria-label").Should().Be("Secciones de Equipo");
        cut.FindAll("a.barra-seccion-enlace").Select(a => a.TextContent.Trim()).Should().Equal("Usuarios", "Roles");
        cut.Find("a[data-pestana=equipo-roles]").GetAttribute("aria-current").Should().Be("page");
    }

    [Theory]
    [InlineData("empresas", Roles.Administrador)]
    [InlineData("documentos", Roles.Cliente)]
    [InlineData("usuarios", Roles.CoordinadorCae)]
    [InlineData("documentos", "(sin rol)")]
    public void Sin_dos_pestanas_visibles_o_fuera_de_una_seccion_no_se_pinta_nada(string url, string rol)
    {
        var cut = Pintar(url, rol);

        cut.FindAll("nav.barra-seccion").Should().BeEmpty();
    }

    [Fact]
    public void Sin_estado_de_autenticacion_no_se_pinta_nada()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("documentos");

        Render<BarraSeccion>().FindAll("nav.barra-seccion").Should().BeEmpty();
    }

    [Fact]
    public void La_barra_sigue_la_navegacion_interna_incluido_el_cambio_de_pestana_de_Documentos()
    {
        var cut = Pintar("documentos", Roles.Administrador);
        var navegacion = Services.GetRequiredService<NavigationManager>();

        navegacion.NavigateTo("documentos?pestana=revision-ia");
        cut.WaitForAssertion(() =>
            cut.Find("a[data-pestana=documentos-revision-ia]").GetAttribute("aria-current").Should().Be("page"));

        navegacion.NavigateTo("incidencias");
        cut.WaitForAssertion(() =>
            cut.Find("a[data-pestana=documentos-incidencias]").GetAttribute("aria-current").Should().Be("page"));

        navegacion.NavigateTo("empresas");
        cut.WaitForAssertion(() => cut.FindAll("nav.barra-seccion").Should().BeEmpty());
    }
}
