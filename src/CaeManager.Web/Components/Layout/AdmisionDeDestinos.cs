using System.Security.Claims;
using CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;
using CaeManager.Application.VistaDemo;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using MediatR;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// Qué destinos de primer nivel ofrece la interfaz FUERA del menú lateral (la paleta Ctrl/Cmd+K y los
/// atajos «g + letra») a quien mira. Es presentación y navegación, nunca autoridad: cada pantalla y
/// cada comando se autorizan por su cuenta. Existe para que el rol Gestor CAE no pulse un destino que
/// la propia interfaz le ofrece y acabe en una pantalla de acceso denegado.
///
/// <para>
/// El criterio es el del menú lateral y no uno paralelo: cada ruta se resuelve contra
/// <see cref="CatalogoMenuLateral.Visibles"/> (enlace o subopción del catálogo, con su grupo, su
/// condición y la lente de demo) y solo las rutas que el menú no tiene —Claves API, Importación,
/// Retención…— llevan aquí los roles que exige su página. Una ruta sin regla no se ofrece (falla
/// cerrado): <c>AdmisionDeDestinosTests</c> comprueba que toda ruta de la paleta y de los atajos la
/// tiene y que ninguna admite a un rol que la página deniega.
/// </para>
/// </summary>
public static class AdmisionDeDestinos
{
    /// <summary>Cómo se decide si un destino se ofrece: por un enlace del catálogo, por una subopción, o por roles propios.</summary>
    /// <param name="FueraDelMenuDeCliente">El rol Cliente no lo recibe aunque la regla lo admita: su menú es fijo (ver <see cref="RutasDelMenuDeCliente"/>).</param>
    private sealed record Regla(
        string? EnlaceId = null, string? SubopcionId = null, string? Roles = null, bool SoloAutenticado = false,
        bool FueraDelMenuDeCliente = false);

    // Las claves son rutas sin barra inicial ni query: lo que devuelve Normalizar.
    private static readonly Dictionary<string, Regla> Reglas = new(StringComparer.OrdinalIgnoreCase)
    {
        // Enlaces del menú: el mismo id, el mismo criterio.
        [""] = new(EnlaceId: "dashboard"),
        ["vision-cartera"] = new(EnlaceId: "vision-cartera"),
        ["direccion"] = new(EnlaceId: "dashboard-ejecutivo"),
        ["dashboard-ejecutivo"] = new(EnlaceId: "indicadores-ia-bpo"),
        ["empresas"] = new(EnlaceId: "empresas"),
        ["subcontratas"] = new(EnlaceId: "subcontratas"),
        ["trabajadores"] = new(EnlaceId: "trabajadores"),
        ["clientes"] = new(EnlaceId: "clientes"),
        ["centros"] = new(EnlaceId: "centros"),
        ["documentos"] = new(EnlaceId: "documentos"),
        ["vehiculos"] = new(EnlaceId: "vehiculos"),
        ["proyectos"] = new(EnlaceId: "proyectos"),
        [CatalogoMenuLateral.RutaMiTrabajo(variosTenants: false)] = new(EnlaceId: "mi-trabajo"),
        ["mi-trabajo"] = new(EnlaceId: "mi-trabajo"),
        ["comunicaciones"] = new(EnlaceId: "comunicaciones"),
        ["gestiones"] = new(EnlaceId: "gestiones"),
        ["visitas"] = new(EnlaceId: "visitas"),
        ["herramientas-pdf"] = new(EnlaceId: "herramientas-pdf"),
        ["alertas"] = new(EnlaceId: "alertas"),
        ["calendario"] = new(EnlaceId: "calendario"),
        ["reportes"] = new(EnlaceId: "reportes"),
        ["facturacion"] = new(EnlaceId: "facturacion"),
        ["configuracion"] = new(EnlaceId: "configuracion"),
        ["auditoria"] = new(EnlaceId: "auditoria"),
        ["delegaciones"] = new(EnlaceId: "delegaciones"),

        // Pestañas de sección sin enlace propio: la subopción del catálogo, que a su vez exige ver el enlace padre.
        ["incidencias"] = new(SubopcionId: "documentos-incidencias"),
        ["plantillas"] = new(SubopcionId: "documentos-plantillas"),
        ["roles"] = new(SubopcionId: "equipo-roles"),

        // Sin entrada en el menú con esta ruta: los roles que exige la página (los vigila el test de paridad).
        ["usuarios"] = new(Roles: CatalogoSeccionesMenu.RolesDeUsuarios),
        ["configuracion/claves-api"] = new(Roles: Roles.Administrador),
        ["tipos-documento"] = new(Roles: Roles.Administrador),
        ["importacion"] = new(Roles: Roles.ConCargaMasivaCsv),
        ["integraciones"] = new(Roles: Roles.Administrador),
        ["retencion"] = new(Roles: Roles.Administrador),
        ["auditoria-ia"] = new(Roles: Roles.Administrador),

        // Páginas personales: las abre cualquier cuenta con sesión, sea cual sea su rol.
        ["mi-avatar"] = new(SoloAutenticado: true),
        ["mi-firma"] = new(SoloAutenticado: true),

        // Vega a pantalla completa: cualquier cuenta con sesión salvo el rol Cliente, cuyo menú
        // fijo no lo lleva. Esto solo decide qué destinos se ofrecen (menú y paleta), no es una
        // restricción: la página exige sesión y nada más, y el panel flotante del layout se le
        // sigue enseñando al rol Cliente como antes. Si el rol Cliente debe tener Vega está por
        // decidir. Lo que cada uno puede pedirle lo deciden los Commands, no la pantalla.
        ["asistente"] = new(SoloAutenticado: true, FueraDelMenuDeCliente: true),
    };

    /// <summary>
    /// Rutas de las páginas que el Encargo de administración no abre cuando es no pleno
    /// (<see cref="PaginasExcluidasDelEncargo"/>), aunque el rol efectivo sea Administrador o Dirección CAE. Un
    /// test las compara con esa lista por reflexión: si divergen, falla.
    /// </summary>
    public static IReadOnlyCollection<string> RutasExcluidasDelEncargo { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "configuracion/claves-api", "auditoria", "auditoria-ia", "importacion", "integraciones", "retencion", "roles",
    };

    /// <summary>
    /// El menú del rol Cliente es fijo y mínimo (marcado en <c>NavMenu.razor</c>, no del catálogo): Inicio, Empresas,
    /// Subcontratas, Centros, Trabajadores y Documentos. Son exactamente los destinos que se le siguen ofreciendo.
    /// </summary>
    public static IReadOnlyCollection<string> RutasDelMenuDeCliente { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "", "empresas", "subcontratas", "centros", "trabajadores", "documentos",
    };

    /// <summary>Rutas con regla: el test de cobertura las compara con las de la paleta y las de los atajos.</summary>
    public static IReadOnlyCollection<string> RutasConRegla => Reglas.Keys;

    /// <summary>Ruta sin barra inicial, sin query ni ancla («/documentos?pestana=x» → «documentos»).</summary>
    public static string Normalizar(string ruta) => ruta.Split('?', '#')[0].Trim('/');

    /// <summary>
    /// El predicado «¿se ofrece esta ruta?» para quien describe <paramref name="contexto"/>. Sin contexto
    /// (ninguna sesión que evaluar: solo ocurre en un host sin estado de autenticación en cascada, nunca
    /// en la aplicación) no se filtra nada: la puerta de cada página decide igualmente.
    /// </summary>
    public static Func<string, bool> Predicado(ContextoMenuLateral? contexto)
    {
        if (contexto is null)
            return _ => true;

        var visibles = CatalogoMenuLateral.Visibles(contexto);
        var enlaces = visibles.SelectMany(g => g.Enlaces).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var subopciones = CatalogoMenuLateral.SubopcionesVisibles(contexto, visibles)
            .Select(s => s.Subopcion.Id).ToHashSet(StringComparer.Ordinal);
        var autenticado = contexto.Usuario.Identity?.IsAuthenticated == true;

        var rigenExclusiones = PaginasExcluidasDelEncargo.RigenLasExclusiones(contexto.Usuario);
        var esCliente = contexto.Usuario.IsInRole(Roles.Cliente);

        return destino =>
        {
            var ruta = Normalizar(destino);
            if (!Reglas.TryGetValue(ruta, out var regla))
                return false;

            // Con encargo no pleno la página se niega aunque el rol efectivo la admita.
            if (rigenExclusiones && RutasExcluidasDelEncargo.Contains(ruta))
                return false;

            if (esCliente && RutasDelMenuDeCliente.Contains(ruta))
                return true;
            if (esCliente && regla.FueraDelMenuDeCliente)
                return false;

            if (regla.EnlaceId is not null)
                return enlaces.Contains(regla.EnlaceId);
            if (regla.SubopcionId is not null)
                return subopciones.Contains(regla.SubopcionId);
            if (regla.Roles is not null)
                return contexto.TieneAlgunRol(regla.Roles);
            return regla.SoloAutenticado && autenticado;
        };
    }

    /// <summary>
    /// El contexto del menú para quien está en la sesión <paramref name="estado"/>, o null si no hay estado
    /// de autenticación. Resuelve lo opcional (lente de demo, Comunicaciones, mediador) del contenedor, y un
    /// fallo al consultarlo nunca rompe la paleta ni un atajo: la consulta de plataforma falla a «no».
    /// </summary>
    /// <param name="consultarPlataforma">Solo la paleta ofrece destinos de plataforma (Delegaciones); los atajos no necesitan la consulta.</param>
    public static async Task<ContextoMenuLateral?> ContextoAsync(
        Task<AuthenticationState>? estado, IServiceProvider servicios, bool consultarPlataforma, CancellationToken ct = default)
    {
        if (estado is null)
            return null;

        var usuario = (await estado).User;

        VistaDemo? vista = null;
        try
        {
            if (servicios.GetService<IVistaDemoActual>() is { } vistaActual)
                vista = (await vistaActual.ObtenerEfectivaAsync(ct))?.Vista;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // La lente solo puede ocultar: sin ella se ofrece lo que el rol real permite.
        }

        var esAdministradorPlataforma = false;
        if (consultarPlataforma && usuario.Identity?.IsAuthenticated == true && servicios.GetService<IMediator>() is { } mediador)
        {
            try
            {
                esAdministradorPlataforma = await mediador.Send(new EsAdministradorPlataformaQuery(), ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Falla cerrado: sin la consulta no se ofrece un destino que solo es de plataforma.
            }
        }

        var comunicacionesActivo = servicios.GetService<IOptions<ComunicacionesOptions>>()?.Value.Activo ?? false;

        return new ContextoMenuLateral(
            usuario, vista, comunicacionesActivo, esAdministradorPlataforma,
            default(PerfilVocabularioTenant), VariosTenants: false);
    }
}
