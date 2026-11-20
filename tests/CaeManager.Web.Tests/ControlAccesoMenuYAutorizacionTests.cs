using System.Reflection;
using System.Security.Claims;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.Inbound.ContratosAcceso.Pages;
using CaeManager.Web.Features.Inbound.ControlAcceso.Pages;
using CaeManager.Web.Features.Inbound.Validacion.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// AF-01: el rol «Control de acceso» (portería del Tenant TCE) solo entra en «Mis Centros». Las páginas de Inbound lo deniegan
/// (403 por <c>[Authorize(Roles)]</c>: contratos, validación y todo lo demás bajo <c>/inbound</c>), y su menú es un único grupo con
/// un único enlace.
/// </summary>
public class ControlAccesoMenuYAutorizacionTests
{
    private const string Ruta = "inbound/control-acceso";

    private static ContextoMenuLateral Contexto(string rol, bool lineaInbound = true) => new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "prueba")),
        null, true, false, PerfilVocabularioTenant.Consultora, false,
        LineaInboundVigente: lineaInbound);

    private static string[] RolesDe(Type pagina) =>
        pagina.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>()
            .SelectMany(a => (a.Roles ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

    [Theory]
    [InlineData(typeof(ContratosAccesoLista))]
    [InlineData(typeof(DocumentosPorValidar))]
    public void El_rol_de_control_de_acceso_recibe_403_en_contratos_y_en_validacion(Type pagina)
    {
        pagina.GetCustomAttributes(typeof(AuthorizeAttribute), true).Should().NotBeEmpty($"{pagina.Name} exige autorización");
        RolesDe(pagina).Should().NotBeEmpty($"{pagina.Name} lista sus roles: un [Authorize] sin roles admitiría a cualquier sesión")
            .And.NotContain(Roles.ControlAcceso);
    }

    [Fact]
    public void Ninguna_pagina_de_Inbound_salvo_Mis_Centros_admite_al_rol_de_control_de_acceso()
    {
        var paginas = typeof(MisCentrosControlAcceso).Assembly.GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t)
                        && t.Namespace is { } ns && ns.StartsWith("CaeManager.Web.Features.Inbound", StringComparison.Ordinal)
                        && t.GetCustomAttributes<RouteAttribute>(true).Any(r => r.Template.StartsWith("/inbound/", StringComparison.Ordinal))
                        && t != typeof(MisCentrosControlAcceso))
            .ToList();

        paginas.Should().NotBeEmpty();
        foreach (var pagina in paginas)
        {
            var autorizados = pagina.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().ToList();
            // Las pantallas de Plataforma se autorizan por política, no por rol: tampoco admiten el rol de negocio.
            autorizados.Should().NotBeEmpty($"{pagina.Name} no puede quedar sin [Authorize]");
            autorizados.Where(a => !string.IsNullOrEmpty(a.Roles) || !string.IsNullOrEmpty(a.Policy)).Should().NotBeEmpty(
                $"{pagina.Name}: un [Authorize] sin roles ni política admite al rol de control de acceso");
            RolesDe(pagina).Should().NotContain(Roles.ControlAcceso, pagina.Name);
        }
    }

    [Fact]
    public void Mis_Centros_solo_admite_al_rol_de_control_de_acceso()
    {
        RolesDe(typeof(MisCentrosControlAcceso)).Should().Equal(Roles.ControlAcceso);
        typeof(MisCentrosControlAcceso).GetCustomAttributes<RouteAttribute>(true).Select(r => r.Template)
            .Should().Contain("/" + Ruta, "el enlace del menú lleva a una página que existe");
    }

    [Fact]
    public void El_menu_del_rol_es_un_unico_grupo_con_un_unico_enlace()
    {
        var visibles = CatalogoMenuLateral.Visibles(Contexto(Roles.ControlAcceso));

        var grupo = visibles.Should().ContainSingle().Which;
        grupo.Grupo.Id.Should().Be("inbound-control-acceso");
        grupo.Enlaces.Should().ContainSingle().Which.Ruta.Should().Be(Ruta);
    }

    [Fact]
    public void Sin_la_linea_Inbound_vigente_el_rol_no_ve_ningun_grupo()
    {
        CatalogoMenuLateral.Visibles(Contexto(Roles.ControlAcceso, lineaInbound: false)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.CoordinadorCae)]
    [InlineData(Roles.GestorCae)]
    [InlineData(Roles.Consulta)]
    [InlineData(Roles.Cliente)]
    public void El_grupo_de_control_de_acceso_es_solo_del_rol_de_control_de_acceso(string rol)
    {
        CatalogoMenuLateral.Visibles(Contexto(rol)).SelectMany(g => g.Enlaces).Select(e => e.Ruta).Should().NotContain(Ruta);
    }
}
