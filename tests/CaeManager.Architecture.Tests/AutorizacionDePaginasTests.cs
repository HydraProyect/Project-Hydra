using CaeManager.Architecture.Tests.Soporte;
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
/// Deliberadamente no verifica QUÉ roles debe llevar cada página (eso sigue
/// siendo juicio semántico del revisor — dos páginas con el mismo patrón de
/// menú pueden merecer roles distintos, ver Clientes/AltaGuiada más abajo,
/// dejadas pendientes a propósito): solo que la declaración exista, para que
/// una página nueva nunca dependa en silencio del FallbackPolicy global.
/// </summary>
public class AutorizacionDePaginasTests
{
    /// <summary>
    /// Excepción explícita y temporal, no un escape genérico: la sesión de
    /// 2026-08-15 que añadió este test (ver CODING_STANDARDS.md § "Checklist
    /// de seguridad para módulos nuevos", ítem "Autorización a nivel de
    /// página") decidió deliberadamente NO tocar estas dos páginas porque su
    /// rol correcto depende de qué operaciones exponen y de un criterio de
    /// producto (¿ve Consulta la lista de Clientes? ¿quién puede completar el
    /// alta guiada?) que no se debía asumir sin más contexto. Siguen
    /// protegidas por el FallbackPolicy global (autenticación) y por
    /// AutorizacionEscrituraBehavior (ningún rol de solo lectura puede
    /// escribir a través de ellas) — el hueco pendiente es de lectura/alcance
    /// de rol, no de escritura. Si esta lista crece más allá de estas dos,
    /// algo se está colando por descuido, no por decisión.
    /// </summary>
    private static readonly string[] PaginasPendientesDeDecisionDeRol =
    [
        "CaeManager.Web.Features.Clientes.Pages.Clientes",
        "CaeManager.Web.Features.Clientes.Pages.AltaGuiada",
    ];

    [Fact]
    public void Toda_pagina_Blazor_declara_Authorize_o_AllowAnonymous_explicito()
    {
        var web = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web");

        var paginas = ReflexionArquitecturaHelper.TiposDe(web)
            .Where(t => !t.IsAbstract && t.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Length > 0)
            .Where(t => !PaginasPendientesDeDecisionDeRol.Contains(t.FullName))
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
}
