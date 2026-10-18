using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// Una pestaña de sección: una pantalla que cuelga de una entrada del menú lateral y se alcanza desde la
/// barra de 48 px bajo la cabecera (<c>BarraSeccion</c>), no desde el menú.
/// </summary>
/// <param name="Id">Identificador estable.</param>
/// <param name="ClaveRotulo">Clave del rótulo en <c>TextosComunes</c>: la barra se pinta en cada idioma.</param>
/// <param name="Ruta">Destino relativo a la base (con su <c>?pestana=</c> si lo lleva).</param>
/// <param name="Roles">
/// Quién ve la pestaña: los mismos roles que la autorización de la pantalla de destino (un test lo
/// compara con su <c>[Authorize]</c>). Solo decide qué pestañas se pintan: la pantalla se autoriza
/// por su cuenta y una pestaña nunca concede nada.
/// </param>
/// <param name="EstaActiva">Si la ruta actual (sin query, sin barras) y su <c>?pestana=</c> son de esta pestaña.</param>
/// <param name="Pagina">
/// La pantalla de destino cuando es una página propia de Administrador que el Encargo de administración no
/// pleno no abre (<see cref="PaginasExcluidasDelEncargo"/>): quien administra por ese encargo no ve la
/// pestaña, porque la pantalla se la negaría. Solo puede ocultar, nunca mostrar.
/// </param>
public sealed record PestanaSeccionMenu(
    string Id,
    string ClaveRotulo,
    string Ruta,
    string Roles,
    Func<string, string?, bool> EstaActiva,
    Type? Pagina = null);

/// <summary>
/// Una sección con pestañas (entrega de Design del 2026-10-17, <c>SECC</c> de <c>marco-talveg.js</c>):
/// varias pantallas bajo una sola entrada del menú lateral.
/// </summary>
/// <param name="EnlaceId">La entrada del menú lateral bajo la que vive; de ella salen el grupo y las migas.</param>
public sealed record SeccionMenu(
    string Id,
    string ClaveRotulo,
    string EnlaceId,
    IReadOnlyList<PestanaSeccionMenu> Pestanas);

/// <summary>Una sección con las pestañas que ve quien mira, y cuál es la actual.</summary>
public sealed record SeccionVisible(SeccionMenu Seccion, IReadOnlyList<PestanaVisible> Pestanas);

public sealed record PestanaVisible(PestanaSeccionMenu Pestana, bool Activa);

/// <summary>
/// Catálogo de las secciones con pestañas. Solo lista pantallas que existen.
/// </summary>
public static class CatalogoSeccionesMenu
{
    // Cada constante es la de la pantalla de destino; SeccionesMenuAutorizacionTests la compara con su
    // [Authorize(Roles = …)]. Una pestaña no puede ofrecer un destino que la pantalla niega.
    internal const string RolesDeIncidencias =
        $"{Roles.Administrador},{Roles.DireccionCae},{Roles.CoordinadorCae},{Roles.GestorCae},{Roles.Consulta}";

    internal const string RolesDeRevisionIa =
        $"{Roles.Administrador},{Roles.DireccionCae},{Roles.CoordinadorCae},{Roles.GestorCae}";

    internal const string RolesDeUsuarios = $"{Roles.Administrador},{Roles.DireccionCae},{Roles.CoordinadorCae}";

    internal const string RolesDeRoles = Roles.Administrador;

    /// <summary>
    /// Todos los roles con menú completo y el rol Cliente: «Todos los documentos» es la propia pantalla
    /// Documentos (<c>[Authorize]</c> sin roles); la barra solo se pinta con dos pestañas visibles o más.
    /// </summary>
    private static readonly string RolesDeDocumentos =
        $"{Roles.Administrador},{Roles.DireccionCae},{Roles.CoordinadorCae},{Roles.GestorCae},{Roles.Consulta},{Roles.Cliente}";

    private const string PestanaRevisionIa = "revision-ia";

    private static bool EsRuta(string ruta, string destino) =>
        ruta.Equals(destino, StringComparison.OrdinalIgnoreCase)
        || ruta.StartsWith(destino + "/", StringComparison.OrdinalIgnoreCase);

    private static bool EsRevisionIa(string ruta, string? pestana) =>
        ruta.Equals("documentos/revision-ia", StringComparison.OrdinalIgnoreCase)
        || (ruta.Equals("documentos", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(pestana, PestanaRevisionIa, StringComparison.OrdinalIgnoreCase)
                // Alias que Documentos acepta si se escribe a mano (Documentos.IdDePestanaDeUrl).
                || string.Equals(pestana, "revision", StringComparison.OrdinalIgnoreCase)));

    public static IReadOnlyList<SeccionMenu> Secciones { get; } =
    [
        // «Todos los documentos» es la pantalla Documentos tal cual (sus pestañas internas —Plataformas CAE,
        // Reclamaciones, Preventivo, Plantillas— siguen dentro de ella). «Revisión IA» e Incidencias salieron
        // de Operación y del propio menú: ahora son pestañas de sección. Revisión IA abre la pestaña de
        // Documentos con su deep-link; /documentos/revision-ia sigue vivo para enlaces guardados.
        new("documentos", "SeccionDocumentos", "documentos",
        [
            new("documentos-todos", "SeccionTodosLosDocumentos", "documentos", RolesDeDocumentos,
                (ruta, pestana) => ruta.Equals("documentos", StringComparison.OrdinalIgnoreCase) && !EsRevisionIa(ruta, pestana)),
            new("documentos-revision-ia", "MenuSubDocumentosRevisionIa", "documentos?pestana=revision-ia", RolesDeRevisionIa,
                EsRevisionIa),
            new("documentos-incidencias", "SeccionIncidencias", "incidencias", RolesDeIncidencias,
                (ruta, _) => EsRuta(ruta, "incidencias")),
        ]),

        // Equipo: Usuarios y Roles. El enlace del menú es «usuarios» (Administración) o «usuarios-equipo»
        // (Coordinador CAE); la sección cuelga del primero, que es el que da el grupo a las migas.
        new("equipo", "SeccionEquipo", "usuarios",
        [
            new("equipo-usuarios", "SeccionUsuarios", "usuarios", RolesDeUsuarios,
                (ruta, _) => EsRuta(ruta, "usuarios")),
            new("equipo-roles", "SeccionRoles", "roles", RolesDeRoles,
                (ruta, _) => EsRuta(ruta, "roles"), typeof(Features.GestionRoles.Pages.Roles)),
            new("equipo-carteras", "SeccionCarterasGestores", "equipo/carteras", RolesDeUsuarios,
                (ruta, _) => EsRuta(ruta, "equipo/carteras"), typeof(Features.CarterasGestores.Pages.CarterasGestores)),
        ]),
    ];

    /// <summary>
    /// La sección a la que pertenece la ruta actual, con las pestañas que <paramref name="contexto"/> puede
    /// ver. <c>null</c> si la ruta no es de ninguna sección o si quedan menos de dos pestañas visibles (una
    /// barra de una sola pestaña no lleva a ninguna parte).
    /// </summary>
    /// <param name="ruta">Ruta relativa a la base, sin query ni barras de los extremos.</param>
    /// <param name="pestana">Valor de <c>?pestana=</c>, si lo hay.</param>
    public static SeccionVisible? Resolver(string ruta, string? pestana, ContextoMenuLateral contexto)
    {
        var seccion = SeccionDe(ruta, pestana);
        if (seccion is null) return null;

        var visibles = seccion.Pestanas
            .Where(p => contexto.TieneAlgunRol(p.Roles))
            .Where(p => !PaginasExcluidasDelEncargo.RigenLasExclusiones(contexto.Usuario) || !PaginasExcluidasDelEncargo.Contiene(p.Pagina))
            .Select(p => new PestanaVisible(p, p.EstaActiva(ruta, pestana)))
            .ToList();

        return visibles.Count >= 2 ? new SeccionVisible(seccion, visibles) : null;
    }

    /// <summary>La sección y la pestaña de la ruta actual, vea quien vea la barra (para las migas).</summary>
    public static (SeccionMenu Seccion, PestanaSeccionMenu Pestana)? PestanaDe(string ruta, string? pestana)
    {
        foreach (var seccion in Secciones)
            foreach (var p in seccion.Pestanas)
                if (p.EstaActiva(ruta, pestana)) return (seccion, p);
        return null;
    }

    private static SeccionMenu? SeccionDe(string ruta, string? pestana) => PestanaDe(ruta, pestana)?.Seccion;
}
