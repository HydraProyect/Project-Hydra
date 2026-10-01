using System.Security.Claims;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Layout;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Reparto y orden por defecto de Negocio, Operación y Control (mandato del propietario del
/// 2026-09-29), sobre <see cref="CatalogoMenuLateral.Visibles"/> directamente, sin pintar el menú:
/// el orden de los tres grupos, que el reagrupado no cambió qué enlaces ve cada rol y que un orden
/// global guardado ANTES del cambio no puede devolver un enlace a su grupo anterior.
/// </summary>
public class CatalogoMenuLateralRepartoTests
{
    private static ContextoMenuLateral Contexto(
        string rol, bool comunicaciones = true, bool adminPlataforma = false, bool variosTenants = false) =>
        new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "prueba")),
            null, comunicaciones, adminPlataforma, PerfilVocabularioTenant.Consultora, variosTenants,
            ParticipaEnIncorporacionCartera: rol is Roles.CoordinadorCae or Roles.GestorCae);

    private static List<string> Ids(IReadOnlyList<CatalogoMenuLateral.GrupoVisible> visibles, string grupo) =>
        visibles.Single(g => g.Grupo.Id == grupo).Enlaces.Select(e => e.Id).ToList();

    [Fact]
    public void El_orden_por_defecto_de_Negocio_Operacion_y_Control_es_el_del_mandato_del_propietario()
    {
        var visibles = CatalogoMenuLateral.Visibles(Contexto(Roles.Administrador));

        visibles.Select(g => g.Grupo.Id).Should().Equal(
            ["dashboards", "negocio", "operacion", "control", "administracion", "plataforma"]);
        Ids(visibles, "negocio").Should().Equal(
            ["empresas", "trabajadores", "documentos", "clientes", "centros", "vehiculos", "proyectos", "subcontratas"]);
        Ids(visibles, "operacion").Should().Equal(
            ["mi-trabajo", "comunicaciones", "gestiones", "visitas", "incidencias"]);
        Ids(visibles, "control").Should().Equal(
            ["alertas", "facturacion", "calendario", "reportes", "conectar-extension"]);
    }

    [Fact]
    public void Con_Comunicaciones_apagado_Operacion_pierde_solo_ese_enlace_y_conserva_el_orden()
    {
        var visibles = CatalogoMenuLateral.Visibles(Contexto(Roles.Administrador, comunicaciones: false));

        Ids(visibles, "operacion").Should().Equal(["mi-trabajo", "gestiones", "visitas", "incidencias"]);
    }

    [Fact]
    public void Mi_trabajo_conserva_su_ruta_contextual_y_su_icono_al_pasar_a_Operacion()
    {
        var miTrabajo = CatalogoMenuLateral.Enlaces.Single(e => e.Id == "mi-trabajo");

        miTrabajo.GrupoId.Should().Be("operacion");
        miTrabajo.Icono.Should().Be("alertas");
        miTrabajo.RutaPara(Contexto(Roles.GestorCae, variosTenants: false)).Should().Be("bandeja");
        miTrabajo.RutaPara(Contexto(Roles.GestorCae, variosTenants: true)).Should().Be("mi-trabajo");
    }

    [Fact]
    public void Cada_enlace_movido_esta_en_su_grupo_nuevo_y_ningun_otro_cambio_de_grupo()
    {
        var grupoPorId = CatalogoMenuLateral.Enlaces.ToDictionary(e => e.Id, e => e.GrupoId);

        grupoPorId["vehiculos"].Should().Be("negocio");
        grupoPorId["proyectos"].Should().Be("negocio");
        grupoPorId["mi-trabajo"].Should().Be("operacion");
        grupoPorId["conectar-extension"].Should().Be("control");
        // El resto de grupos y de enlaces, tal como estaban.
        grupoPorId.Where(p => p.Value is "dashboards" or "administracion" or "plataforma")
            .Select(p => p.Key).Should().BeEquivalentTo(
            [
                "dashboard", "vision-cartera", "solicitudes-cartera", "dashboard-ejecutivo",
                "usuarios", "configuracion", "verificacion-dos-pasos",
                "delegaciones", "estado-comercial", "conectores-cae",
            ]);
    }

    /// <summary>
    /// El reagrupado no da ni quita visibilidad: el conjunto de enlaces que ve cada rol es el de
    /// antes (lista literal, escrita a partir de las Condicion y las Visible previas al cambio),
    /// con independencia de en qué grupo esté cada miTrabajo.
    /// </summary>
    [Theory]
    [InlineData(Roles.Administrador, true)]
    [InlineData(Roles.DireccionCae, false)]
    [InlineData(Roles.CoordinadorCae, false)]
    [InlineData(Roles.GestorCae, false)]
    [InlineData(Roles.Consulta, false)]
    public void El_reagrupado_no_cambia_que_enlaces_ve_cada_rol(string rol, bool adminPlataforma)
    {
        var visibles = CatalogoMenuLateral.Visibles(Contexto(rol, adminPlataforma: adminPlataforma));
        var vistos = visibles.SelectMany(g => g.Enlaces).Select(e => e.Id).ToList();

        vistos.Should().OnlyHaveUniqueItems();
        vistos.Should().BeEquivalentTo(EnlacesVisiblesAntesDelReagrupado(rol));
    }

    [Theory]
    [InlineData(Roles.Cliente)]
    [InlineData("(sin rol)")]
    public void Sin_rol_de_menu_completo_no_se_ve_ningun_grupo(string rol)
    {
        CatalogoMenuLateral.Visibles(Contexto(rol)).Should().BeEmpty();
    }

    private static string[] EnlacesVisiblesAntesDelReagrupado(string rol)
    {
        var comunes = new List<string>
        {
            "dashboard",
            "empresas", "subcontratas", "trabajadores", "clientes", "centros", "documentos", "conectar-extension",
            "comunicaciones", "gestiones", "incidencias", "visitas", "vehiculos", "proyectos",
            "mi-trabajo", "alertas", "calendario", "reportes",
        };
        var administracionAmpliada = rol is Roles.Administrador or Roles.DireccionCae;
        if (administracionAmpliada) comunes.AddRange(["facturacion", "usuarios"]);
        // D-12: el Coordinador CAE llega a /usuarios (ya autorizado por rol) desde Control; no es un permiso nuevo.
        if (rol == Roles.CoordinadorCae) comunes.Add("usuarios-equipo");
        if (rol is Roles.Administrador or Roles.DireccionCae or Roles.CoordinadorCae) comunes.Add("vision-cartera");
        if (rol is Roles.CoordinadorCae or Roles.GestorCae) comunes.Add("solicitudes-cartera");
        if (rol is Roles.Administrador or Roles.DireccionCae or Roles.Consulta) comunes.Add("dashboard-ejecutivo");
        if (rol == Roles.Administrador)
            comunes.AddRange(
                ["configuracion", "verificacion-dos-pasos", "delegaciones", "estado-comercial", "conectores-cae"]);
        return [.. comunes];
    }

    /// <summary>
    /// Un orden global guardado antes del cambio (lista plana con el orden previo del catálogo)
    /// no devuelve los enlaces movidos a su grupo anterior: el grupo lo fija el catálogo, dentro
    /// del grupo manda la posición relativa guardada, y no desaparece ni se duplica ningún enlace.
    /// </summary>
    [Fact]
    public void Un_orden_guardado_antiguo_coloca_los_enlaces_movidos_en_su_grupo_nuevo_sin_perder_ni_duplicar()
    {
        string[] gruposAntiguos = ["dashboards", "negocio", "operacion", "control", "administracion", "plataforma"];
        string[] enlacesAntiguos =
        [
            "dashboard", "vision-cartera", "solicitudes-cartera", "dashboard-ejecutivo",
            "empresas", "subcontratas", "trabajadores", "clientes", "centros", "documentos", "conectar-extension",
            "comunicaciones", "gestiones", "incidencias", "visitas", "vehiculos", "proyectos",
            "mi-trabajo", "alertas", "facturacion", "calendario", "reportes",
            "usuarios", "configuracion", "verificacion-dos-pasos",
            "delegaciones", "estado-comercial", "conectores-cae",
        ];
        var contexto = Contexto(Roles.Administrador, adminPlataforma: true);

        var visibles = CatalogoMenuLateral.Visibles(contexto, gruposAntiguos, enlacesAntiguos);

        Ids(visibles, "negocio").Should().Equal(
            ["empresas", "subcontratas", "trabajadores", "clientes", "centros", "documentos", "vehiculos", "proyectos"]);
        Ids(visibles, "operacion").Should().Equal(
            ["comunicaciones", "gestiones", "incidencias", "visitas", "mi-trabajo"]);
        Ids(visibles, "control").Should().Equal(
            ["conectar-extension", "alertas", "facturacion", "calendario", "reportes"]);

        var sinOrden = CatalogoMenuLateral.Visibles(contexto).SelectMany(g => g.Enlaces).Select(e => e.Id).ToList();
        var conOrden = visibles.SelectMany(g => g.Enlaces).Select(e => e.Id).ToList();
        conOrden.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(sinOrden, "el orden nunca añade ni quita");
        conOrden.Should().HaveCount(sinOrden.Count);
    }
}
