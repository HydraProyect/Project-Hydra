using CaeManager.Architecture.Tests.Soporte;
using CaeManager.Web.Features.Comunicaciones;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// CODING_STANDARDS.md § "Checklist de seguridad para módulos nuevos", ítem
/// "Autorización a nivel de página": un escaneo de texto (2026-08-15) encontró
/// 21 de 52 páginas de CaeManager.Web sin <c>@attribute [Authorize</c> como
/// substring literal, confiando en apariencia solo en el FallbackPolicy
/// global (Program.cs — exige sesión iniciada, no rol). Revisar caso por caso
/// esa misma sesión encontró que el escaneo de texto daba falsos positivos
/// (PoliticaPrivacidad/TerminosCondiciones sí llevaban
/// <c>[Microsoft.AspNetCore.Authorization.AllowAnonymous]</c> cualificado, que
/// un grep del substring corto no ve) y falsos negativos reales: media
/// docena de pantallas troncales (Gestiones, Incidencias, Proyectos,
/// Vehiculos, Visitas...) dependían solo de que el enlace no apareciera en el
/// menú del rol Cliente (NavMenu.razor) para no ser alcanzadas — exactamente
/// el patrón del hallazgo de Comunicaciones (Fase 60): una pantalla real
/// accesible solo por escribir la URL.
///
/// A diferencia de lo que el propio ítem del checklist da por hecho, esto SÍ
/// es mecanizable: <c>@page</c> compila a <see cref="RouteAttribute"/> y
/// <c>@attribute [Authorize]</c>/<c>[AllowAnonymous]</c> compilan a atributos
/// reales sobre la clase generada — visibles por reflexión sobre el
/// ensamblado ya compilado, sin parsear el .razor como texto ni sufrir los
/// falsos positivos/negativos de un grep.
///
/// Deliberadamente no verifica QUÉ roles debe llevar cada página en general
/// (eso sigue siendo juicio semántico del revisor): solo que la declaración
/// exista, para que una página nueva nunca dependa en silencio del
/// FallbackPolicy global. La excepción es Clientes/AltaGuiada (ver más abajo):
/// ahí sí se fija el conjunto exacto de roles, porque quedaron pendientes de
/// decisión de producto (DEC-1, plan de sesiones nocturnas 2026-09-02) y una
/// vez decidido el ratchet debe poder detectar que alguien lo amplíe o lo
/// reduzca sin querer.
/// </summary>
public class AutorizacionDePaginasTests
{
    [Fact]
    public void Toda_pagina_Blazor_declara_Authorize_o_AllowAnonymous_explicito()
    {
        var web = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web");

        var paginas = ReflexionArquitecturaHelper.TiposDe(web)
            .Where(t => !t.IsAbstract && t.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Length > 0)
            .ToList();

        var infractores = paginas
            .Where(t =>
                t.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false).Length == 0 &&
                t.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false).Length == 0)
            .Select(t => t.FullName)
            .OrderBy(nombre => nombre, StringComparer.Ordinal)
            .ToList();

        infractores.Should().BeEmpty(
            "toda @page debe declarar @attribute [Authorize] (con o sin Roles=, según a quién esté enlazada desde " +
            "NavMenu.razor) o, si es deliberadamente pública, @attribute [AllowAnonymous] — sin esto, la página " +
            "queda accesible solo por escribir la URL con el único filtro del FallbackPolicy global (autenticación, " +
            "no rol), el mismo patrón del hallazgo de Comunicaciones (Fase 60, CODING_STANDARDS.md)");
    }

    /// <summary>
    /// DEC-1 (plan de sesiones nocturnas 2026-09-02): los cinco roles de
    /// gestión CAE —nunca el rol <c>Cliente</c> externo— pueden leer
    /// <c>/clientes</c> y <c>/clientes/alta-guiada</c>. Es el mismo conjunto
    /// que <see cref="RolesComunicaciones.Gestion"/> y que
    /// <c>RolesConMenuCompleto</c> de <c>NavMenu.razor</c>.
    ///
    /// <b>No es <c>RolesDeCartera</c> de <c>NavMenu.razor</c></b> —Administrador,
    /// DireccionCae, CoordinadorCae, sin GestorCae ni Consulta— aunque el
    /// nombre invite a confundirlos: esa constante gatea la entrada de menú
    /// "Visión de cartera" (un dashboard agregado), no el acceso a la lista de
    /// Clientes. La primera versión de este test usaba esa constante por
    /// error y habría dado 403 a GestorCae sobre su propia cartera —su trabajo
    /// diario, ver <c>AlcanceDatosService.ObtenerClienteIdsDeCarteraAsync</c>—
    /// y a Consulta sobre su supervisión de solo lectura, rompiendo
    /// silenciosamente <see cref="CaeManager.E2ETests.AlcanceRolesTests"/>.
    ///
    /// Comprueba el conjunto exacto de roles, no solo que <c>[Authorize]</c>
    /// exista: ese caso ya lo cubre el test anterior, y un ratchet que solo
    /// mira "¿hay atributo?" no detecta que alguien reduzca el rol y deje
    /// fuera a GestorCae, o lo amplíe a Cliente, mañana.
    /// </summary>
    [Theory]
    [InlineData("CaeManager.Web.Features.Clientes.Pages.Clientes")]
    [InlineData("CaeManager.Web.Features.Clientes.Pages.AltaGuiada")]
    public void Clientes_y_AltaGuiada_solo_permiten_roles_de_gestion_cae(string nombreCompletoDeLaPagina)
    {
        var web = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web");
        var pagina = ReflexionArquitecturaHelper.TiposDe(web).Single(t => t.FullName == nombreCompletoDeLaPagina);

        var autorizacion = pagina.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Should().ContainSingle("la página debe declarar exactamente un [Authorize] con Roles=")
            .Subject;

        var rolesDeclarados = (autorizacion.Roles ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        var rolesDeGestionCae = RolesComunicaciones.Gestion
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        rolesDeclarados.Should().BeEquivalentTo(rolesDeGestionCae,
            "DEC-1 fija los cinco roles de gestión CAE (Administrador, DireccionCae, CoordinadorCae, GestorCae, " +
            "Consulta) como los únicos con lectura de Clientes/AltaGuiada — ni más amplio (Cliente vería datos de " +
            "otras organizaciones) ni más estrecho (GestorCae o Consulta perderían un acceso que ya tenían y que " +
            "AlcanceDatosService ya acota correctamente por su lado)");
    }
}
