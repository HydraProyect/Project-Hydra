using System.Reflection;
using System.Security.Claims;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.AtajosGlobales;
using CaeManager.Web.Features.BusquedaGlobal;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// Un Gestor CAE no sabe qué es un rol y no debe encontrarse una pantalla de acceso denegado desde algo que la
/// propia interfaz le ofrece. <see cref="AdmisionDeDestinos"/> decide qué destinos de la paleta Ctrl/Cmd+K y de los
/// atajos «g + letra» se ofrecen a cada rol, con el criterio del menú lateral. Es presentación: no autoriza nada,
/// y estos tests lo confirman comparando con el <c>[Authorize(Roles=…)]</c> real de cada página.
/// </summary>
public class AdmisionDeDestinosTests
{
    private static readonly string[] TodosLosRoles =
        [Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae, Roles.Consulta, Roles.Cliente];

    private static ContextoMenuLateral ContextoDe(
        string? rol, bool comunicacionesActivo = true, bool plataforma = false, params string[] claimsDeEncargo)
    {
        var identidad = rol is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity([new Claim(ClaimTypes.Role, rol), .. claimsDeEncargo.Select(c => new Claim(c, "true"))], authenticationType: "prueba");
        return new ContextoMenuLateral(
            new ClaimsPrincipal(identidad), Vista: null, comunicacionesActivo, plataforma, default(PerfilVocabularioTenant),
            VariosTenants: false);
    }

    private static IEnumerable<string> RutasDeLaPaleta => CoberturaDePaleta.DestinosNavegacion.Select(d => d.Ruta);

    private static IEnumerable<string> RutasDeLosAtajos => CatalogoAtajos.DestinosNavegacion.Values;

    // ---------------------------------------------------------------- cobertura

    [Fact]
    public void Toda_ruta_de_la_paleta_y_de_los_atajos_tiene_regla()
    {
        // Una ruta sin regla no se ofrece a nadie (falla cerrado): una entrada nueva en la paleta sin regla
        // desaparecería sin que nadie lo notara, y este test la nombra.
        var sinRegla = RutasDeLaPaleta.Concat(RutasDeLosAtajos)
            .Select(AdmisionDeDestinos.Normalizar)
            .Where(r => !AdmisionDeDestinos.RutasConRegla.Contains(r, StringComparer.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        sinRegla.Should().BeEmpty("cada destino de la paleta y de los atajos necesita su regla en AdmisionDeDestinos");
    }

    [Fact]
    public void Toda_regla_se_usa_en_la_paleta_o_en_los_atajos()
    {
        var usadas = RutasDeLaPaleta.Concat(RutasDeLosAtajos).Select(AdmisionDeDestinos.Normalizar)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AdmisionDeDestinos.RutasConRegla.Where(r => !usadas.Contains(r)).Should().BeEmpty(
            "una regla que ningún destino usa es una regla que nadie mantiene");
    }

    // ----------------------------------------------- paridad con la página real

    /// <summary>
    /// La página de <paramref name="ruta"/>: la de plantilla exacta («/clientes»), no una de detalle («/clientes/{id}»); solo
    /// si no hay exacta, la de plantilla con parámetros opcionales («/configuracion/{EntradaRuta?}»).
    /// </summary>
    private static Type? PaginaDe(string ruta)
    {
        var destino = AdmisionDeDestinos.Normalizar(ruta);
        var candidatas = typeof(AdmisionDeDestinos).Assembly.GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t))
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>().Select(r => (Tipo: t, Plantilla: r.Template)))
            .ToList();

        var exactas = candidatas.Where(c => string.Equals(c.Plantilla.Trim('/'), destino, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exactas.Count > 0)
            return exactas.Single().Tipo;

        var conParametros = candidatas.Where(c => c.Plantilla.Contains('{')
            && string.Equals(c.Plantilla.Split('{')[0].Trim('/'), destino, StringComparison.OrdinalIgnoreCase)).ToList();
        return conParametros.Count == 1 ? conParametros[0].Tipo : null;
    }

    /// <summary>Los roles que exige la página de <paramref name="ruta"/> (null si basta con tener sesión), o «no encontrada».</summary>
    private static (bool Encontrada, string? Roles) RolesDeLaPagina(string ruta)
    {
        var pagina = PaginaDe(ruta);
        if (pagina is null)
            return (false, null);

        var roles = string.Join(',', pagina.GetCustomAttributes<AuthorizeAttribute>()
            .Select(a => a.Roles).Where(r => !string.IsNullOrWhiteSpace(r)));
        return (true, roles.Length == 0 ? null : roles);
    }
    public static TheoryData<string> RutasOfrecidas => [.. RutasDeLaPaleta.Concat(RutasDeLosAtajos).Distinct()];

    [Theory]
    [MemberData(nameof(RutasOfrecidas))]
    public void Ningun_rol_recibe_un_destino_que_la_pagina_le_deniega_por_rol(string ruta)
    {
        var (encontrada, rolesDeLaPagina) = RolesDeLaPagina(ruta);
        encontrada.Should().BeTrue($"{ruta} debe ser una página con @page");

        if (rolesDeLaPagina is null)
            return;

        var admitidos = rolesDeLaPagina.Split(',', StringSplitOptions.TrimEntries);
        foreach (var rol in TodosLosRoles.Where(r => !admitidos.Contains(r)))
        {
            AdmisionDeDestinos.Predicado(ContextoDe(rol))(ruta).Should().BeFalse(
                $"la página {ruta} deniega el rol {rol}: ofrecérselo lo dejaría en «acceso denegado»");
        }
    }

    // -------------------------------------------------------------- por rol

    [Theory]
    [InlineData("/direccion")]
    [InlineData("/facturacion")]
    [InlineData("/auditoria")]
    [InlineData("/auditoria-ia")]
    [InlineData("/roles")]
    [InlineData("/usuarios")]
    [InlineData("/configuracion")]
    [InlineData("/configuracion/claves-api")]
    [InlineData("/tipos-documento")]
    [InlineData("/importacion")]
    [InlineData("/integraciones")]
    [InlineData("/retencion")]
    [InlineData("/delegaciones")]
    [InlineData("/dashboard-ejecutivo")]
    [InlineData("/vision-cartera")]
    public void El_Gestor_CAE_no_recibe_destinos_de_direccion_ni_de_administracion(string ruta)
    {
        AdmisionDeDestinos.Predicado(ContextoDe(Roles.GestorCae))(ruta).Should().BeFalse();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/mi-trabajo")]
    [InlineData("/bandeja")]
    [InlineData("/clientes")]
    [InlineData("/empresas")]
    [InlineData("/trabajadores")]
    [InlineData("/documentos")]
    [InlineData("/incidencias")]
    [InlineData("/plantillas")]
    [InlineData("/visitas")]
    [InlineData("/gestiones")]
    [InlineData("/alertas")]
    [InlineData("/comunicaciones")]
    [InlineData("/mi-firma")]
    [InlineData("/mi-avatar")]
    public void El_Gestor_CAE_conserva_sus_destinos_de_trabajo(string ruta)
    {
        AdmisionDeDestinos.Predicado(ContextoDe(Roles.GestorCae))(ruta).Should().BeTrue();
    }

    [Fact]
    public void El_Administrador_recibe_todos_los_destinos_de_la_paleta_salvo_los_de_otro_rol()
    {
        var admite = AdmisionDeDestinos.Predicado(ContextoDe(Roles.Administrador));

        var ocultos = RutasDeLaPaleta.Where(r => !admite(r)).ToList();

        // Las páginas no niegan nada al Administrador: desde el 2026-10-19 también lee el Cuadro de dirección.
        ocultos.Should().BeEmpty();
    }

    [Fact]
    public void Sin_sesion_no_se_ofrece_ningun_destino()
    {
        var admite = AdmisionDeDestinos.Predicado(ContextoDe(rol: null));

        RutasDeLaPaleta.Concat(RutasDeLosAtajos).Where(admite).Should().BeEmpty();
    }

    [Fact]
    public void Delegaciones_se_ofrece_con_capacidad_de_plataforma_aunque_no_se_tenga_el_rol_Administrador()
    {
        AdmisionDeDestinos.Predicado(ContextoDe(Roles.Consulta, plataforma: true))("/delegaciones").Should().BeTrue();
        AdmisionDeDestinos.Predicado(ContextoDe(Roles.Consulta, plataforma: false))("/delegaciones").Should().BeFalse();
    }

    [Fact]
    public void Comunicaciones_no_se_ofrece_con_el_modulo_congelado()
    {
        AdmisionDeDestinos.Predicado(ContextoDe(Roles.GestorCae, comunicacionesActivo: false))("/comunicaciones")
            .Should().BeFalse("el menú lateral tampoco lo pinta: Bandeja.razor redirige a /not-found");
    }

    [Fact]
    public void La_ruta_se_compara_sin_query_ni_barra()
    {
        var admite = AdmisionDeDestinos.Predicado(ContextoDe(Roles.GestorCae));

        admite("/documentos?pestana=plantillas").Should().BeTrue();
        admite("documentos/").Should().BeTrue();
        admite("/direccion?x=1").Should().BeFalse();
    }

    [Fact]
    public void Una_ruta_desconocida_no_se_ofrece()
    {
        AdmisionDeDestinos.Predicado(ContextoDe(Roles.Administrador))("/pantalla-que-no-existe").Should().BeFalse();
    }

    [Fact]
    public void Sin_contexto_no_se_filtra_nada()
    {
        AdmisionDeDestinos.Predicado(null)("/direccion").Should().BeTrue();
    }

    // ------------------------------------------- Encargo de administración no pleno

    [Fact]
    public void Las_rutas_excluidas_del_encargo_son_las_de_las_paginas_que_el_encargo_no_abre()
    {
        var destinos = RutasDeLaPaleta.Concat(RutasDeLosAtajos).Select(AdmisionDeDestinos.Normalizar).Distinct().ToList();

        foreach (var ruta in destinos)
        {
            var pagina = PaginaDe(ruta);
            pagina.Should().NotBeNull($"/{ruta} debe tener una página");

            AdmisionDeDestinos.RutasExcluidasDelEncargo.Contains(ruta, StringComparer.OrdinalIgnoreCase)
                .Should().Be(PaginasExcluidasDelEncargo.Contiene(pagina),
                    $"/{ruta}: AdmisionDeDestinos y PaginasExcluidasDelEncargo deben coincidir");
        }
    }

    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    public void Con_encargo_no_pleno_no_se_ofrece_lo_que_el_encargo_no_abre(string rol)
    {
        var admite = AdmisionDeDestinos.Predicado(ContextoDe(rol, true, false, RolEfectivoDelWorkspaceMiddleware.TipoClaimEncargoAdministracion));

        foreach (var ruta in AdmisionDeDestinos.RutasExcluidasDelEncargo)
            admite("/" + ruta).Should().BeFalse($"/{ruta}: el encargo no pleno no abre esa página aunque el rol efectivo sea {rol}");

        admite("/empresas").Should().BeTrue("lo que el encargo sí abre se sigue ofreciendo");
    }

    [Fact]
    public void Con_encargo_pleno_no_se_reserva_nada()
    {
        var admite = AdmisionDeDestinos.Predicado(ContextoDe(Roles.Administrador, true, false,
            RolEfectivoDelWorkspaceMiddleware.TipoClaimEncargoAdministracion, RolEfectivoDelWorkspaceMiddleware.TipoClaimEncargoPleno));

        AdmisionDeDestinos.RutasExcluidasDelEncargo.Where(r => !admite("/" + r)).Should().BeEmpty();
    }

    // ------------------------------------------------------------- rol Cliente

    [Fact]
    public void El_rol_Cliente_conserva_los_destinos_de_su_menu_fijo_y_ningun_otro()
    {
        var admite = AdmisionDeDestinos.Predicado(ContextoDe(Roles.Cliente));

        // Inicio, Empresas, Subcontratas, Centros, Trabajadores y Documentos (NavMenu.razor), más lo personal.
        string[] esperados = ["/", "/empresas", "/subcontratas", "/centros", "/trabajadores", "/documentos", "/mi-avatar", "/mi-firma"];
        RutasDeLaPaleta.Concat(RutasDeLosAtajos).Distinct().Where(admite).Should().BeEquivalentTo(esperados);
    }
    [Fact]
    public void El_menu_lateral_y_la_paleta_coinciden_en_los_enlaces_de_cada_rol()
    {
        // Mismo criterio: un destino de la paleta que es también un enlace del menú se ofrece en ambos o en ninguno.
        // El rol Cliente no usa el catálogo: su menú es fijo (NavMenu.razor) y lo cubre otro test.
        foreach (var rol in TodosLosRoles.Where(r => r != Roles.Cliente))
        {
            var contexto = ContextoDe(rol);
            var idsEnMenu = CatalogoMenuLateral.Visibles(contexto).SelectMany(g => g.Enlaces).Select(e => e.Id).ToHashSet();
            var admite = AdmisionDeDestinos.Predicado(contexto);

            // Dos enlaces pueden compartir ruta («Equipo» de Administración y el del Coordinador CAE): la ruta se
            // ofrece si alguno de ellos se ve.
            foreach (var grupo in CatalogoMenuLateral.Enlaces.GroupBy(e => "/" + AdmisionDeDestinos.Normalizar(e.Ruta)))
            {
                if (!RutasDeLaPaleta.Contains(grupo.Key, StringComparer.OrdinalIgnoreCase))
                    continue;

                admite(grupo.Key).Should().Be(grupo.Any(e => idsEnMenu.Contains(e.Id)),
                    $"«{grupo.Key}» para {rol}: el menú y la paleta no pueden divergir");
            }
        }
    }
}