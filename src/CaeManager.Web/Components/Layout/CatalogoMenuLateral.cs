using System.Security.Claims;
using CaeManager.Application.VistaDemo;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// Lo que el menú lateral necesita saber de quien lo mira para decidir qué enlaces existen. No
/// es autoridad: cada pantalla y cada comando se autorizan por su cuenta (ver
/// <see cref="MenuPorVista"/>); esto solo decide qué pestañas se pintan.
/// </summary>
public sealed record ContextoMenuLateral(
    ClaimsPrincipal Usuario,
    VistaDemo? Vista,
    bool ComunicacionesActivo,
    bool EsAdministradorPlataforma,
    PerfilVocabularioTenant Perfil,
    bool VariosTenants)
{
    /// <summary>
    /// Mismo criterio que <c>&lt;AuthorizeView Roles="…"&gt;</c>, que era como el marcado lo
    /// decidía: la lista separada por comas, acotada por la lente de demo (que solo puede
    /// ocultar), y basta con uno de los roles.
    /// </summary>
    public bool TieneAlgunRol(string roles) =>
        MenuPorVista.Acotar(Vista, roles)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(Usuario.IsInRole);
}

/// <summary>Un grupo del menú lateral (<c>&lt;details data-grupo="…"&gt;</c>).</summary>
/// <param name="Id">Identificador estable: es el <c>data-grupo</c> que persiste menu-lateral.js y la clave del orden guardado.</param>
/// <param name="AbiertoPorDefecto">Si nace abierto en el primer render (menu-lateral.js respeta lo que el usuario haya tocado).</param>
/// <param name="Visible">Si el grupo existe para quien mira; sus enlaces solo pueden restringirlo más.</param>
public sealed record GrupoMenuLateral(
    string Id,
    string Titulo,
    bool AbiertoPorDefecto,
    Func<ContextoMenuLateral, bool> Visible);

/// <summary>Un enlace del menú lateral. Pertenece a un único grupo y no puede cambiar de grupo.</summary>
/// <param name="Id">Identificador estable, clave del orden guardado; no cambia aunque cambie la ruta o el rótulo.</param>
/// <param name="Condicion">Restricción propia además de la del grupo; null si basta con ver el grupo.</param>
/// <param name="CoincidenciaExacta">NavLinkMatch.All en vez de prefijo (el Dashboard, cuya ruta vacía casaría con todo).</param>
/// <param name="RotuloPorContexto">Rótulo que depende del contexto (perfil de vocabulario); si es null, <paramref name="Rotulo"/>.</param>
/// <param name="RutaPorContexto">Ruta que depende del contexto; si es null, <paramref name="Ruta"/>.</param>
public sealed record EnlaceMenuLateral(
    string Id,
    string GrupoId,
    string Ruta,
    string Icono,
    string Rotulo,
    Func<ContextoMenuLateral, bool>? Condicion = null,
    bool CoincidenciaExacta = false,
    Func<ContextoMenuLateral, string>? RotuloPorContexto = null,
    Func<ContextoMenuLateral, string>? RutaPorContexto = null)
{
    public string RotuloPara(ContextoMenuLateral contexto) => RotuloPorContexto?.Invoke(contexto) ?? Rotulo;
    public string RutaPara(ContextoMenuLateral contexto) => RutaPorContexto?.Invoke(contexto) ?? Ruta;
}

/// <summary>
/// Catálogo del menú lateral de los usuarios internos: cada grupo y cada enlace con su
/// identificador estable, su regla de visibilidad y su posición por defecto (el orden de las
/// listas). El menú del rol Cliente NO está aquí: es deliberadamente distinto y mínimo (Fase 31),
/// fijo, y vive como marcado en <c>NavMenu.razor</c>.
///
/// <para>
/// Solo se listan los módulos que ya existen y funcionan (ver ROADMAP.md): un enlace a una
/// pantalla que todavía no existe es peor que no tener el enlace. Los datos que ve cada enlace ya
/// quedan acotados por IAlcanceDatosService en las Queries — este catálogo solo decide qué
/// pestañas existen, no qué filas aparecen dentro. Un único set de iconos outline (ver
/// DESIGN_SYSTEM.md, "Iconografía").
/// </para>
/// </summary>
public static class CatalogoMenuLateral
{
    private const string RolesConMenuCompleto =
        $"{Roles.Administrador},{Roles.DireccionCae},{Roles.CoordinadorCae},{Roles.GestorCae},{Roles.Consulta}";

    private const string RolesDeAdministracionAmpliada = $"{Roles.Administrador},{Roles.DireccionCae}";

    /// <summary>
    /// Distinto de <see cref="RolesDeAdministracionAmpliada"/> a propósito: Consulta ve todo el
    /// negocio en solo lectura (Roles.cs) y el Dashboard actual ya le mostraba "Empresas en
    /// riesgo" — al migrar esa pieza a Dashboard Ejecutivo (docs/blueprints/OPERATIONAL-HOME.md
    /// § 7) no debía perder esa visibilidad. No se añade Consulta al rol de administración
    /// ampliada porque ese también gatea Facturación y Administración, que Consulta no debe ver.
    /// </summary>
    private const string RolesDeDashboardEjecutivo = $"{Roles.Administrador},{Roles.DireccionCae},{Roles.Consulta}";

    private const string RolesDeCartera = $"{Roles.Administrador},{Roles.DireccionCae},{Roles.CoordinadorCae}";

    public static IReadOnlyList<GrupoMenuLateral> Grupos { get; } =
    [
        // Dashboard / Visión de cartera / Dashboard Ejecutivo son tres Operational Home distintos
        // (DDL-004 § 1.1), con conjuntos de roles no anidados (Consulta ve Dashboard+Ejecutivo pero
        // no Cartera; CoordinadorCae ve Dashboard+Cartera pero no Ejecutivo) — no se pliegan en
        // pestañas de una sola pantalla porque Pestanas no autoriza por pestaña hoy y sus layouts
        // son incompatibles. Agruparlos solo bajo un mismo encabezado visual "Dashboards" (informe
        // de consolidación de menú, 2026-09-01 § 6) deja esa pregunta de producto abierta.
        new("dashboards", "Dashboards", AbiertoPorDefecto: true, c => c.TieneAlgunRol(RolesConMenuCompleto)),

        // Orden por DDL-073 (03_INFORMATION_ARCHITECTURE.md § 3.2): Negocio sigue la cadena de
        // titularidad, de lo mío a lo ajeno, con Documentos al final por ser la vista transversal.
        new("negocio", "Negocio", AbiertoPorDefecto: true, c => c.TieneAlgunRol(RolesConMenuCompleto)),

        // Orden por DDL-073: Operación sigue la frecuencia de uso diario.
        new("operacion", "Operación", AbiertoPorDefecto: true, c => c.TieneAlgunRol(RolesConMenuCompleto)),

        // Colapsado por defecto (informe de consolidación de menú, 2026-09-01 § 6): Control es, por
        // el propio orden de DDL-073, el grupo de uso más esporádico frente a Negocio/Operación.
        new("control", "Control", AbiertoPorDefecto: false, c => c.TieneAlgunRol(RolesConMenuCompleto)),

        // Un único grupo visual para dos audiencias: DireccionCae solo ve Usuarios; solo
        // Administrador ve el resto (Condicion de cada enlace).
        new("administracion", "Administración", AbiertoPorDefecto: true, c => c.TieneAlgunRol(RolesDeAdministracionAmpliada)),

        // Delegaciones y Estado comercial: fuera de "administracion" a propósito (#401/#403,
        // revisión adversarial de Codex). Ambas páginas se autorizan por CAPACIDAD (`[Authorize]`
        // sin Roles: el claim de rol se retira bajo sesión privilegiada), así que el grupo aparece
        // también para una sesión AdminPlataforma sin el rol Administrador — mismo criterio que
        // CrearClienteDeleganteCommand y el botón "Nueva delegación" (EsAdministradorPlataformaQuery:
        // "si divergieran, el botón diría una cosa y el comando otra"). Delegaciones es doble
        // audiencia y Estado comercial es solo de plataforma (su Query falla cerrado a lista vacía
        // sin la capacidad): comparten el mismo gate aceptando que un Administrador normal vea
        // "Estado comercial" vacío.
        new("plataforma", "Plataforma", AbiertoPorDefecto: true,
            c => c.Usuario.Identity?.IsAuthenticated == true
                 && (c.TieneAlgunRol(Roles.Administrador) || c.EsAdministradorPlataforma)),
    ];

    public static IReadOnlyList<EnlaceMenuLateral> Enlaces { get; } =
    [
        new("dashboard", "dashboards", "", "dashboard", "Dashboard", CoincidenciaExacta: true),
        new("vision-cartera", "dashboards", "vision-cartera", "cartera", "Visión de cartera",
            Condicion: c => c.TieneAlgunRol(RolesDeCartera)),
        new("dashboard-ejecutivo", "dashboards", "dashboard-ejecutivo", "dashboard", "Dashboard Ejecutivo",
            Condicion: c => c.TieneAlgunRol(RolesDeDashboardEjecutivo)),

        // DDL-072: "Mi empresa" (registro único) en perfil Cliente Directo, "Empresas" (lista) en
        // perfil Consultora — la misma entrada bajo los dos perfiles, solo cambia el rótulo.
        new("empresas", "negocio", "empresas", "empresas", "Empresas",
            RotuloPorContexto: c => c.Perfil == PerfilVocabularioTenant.ClienteDirecto ? "Mi empresa" : "Empresas"),
        new("subcontratas", "negocio", "subcontratas", "subcontratas", "Subcontratas"),
        new("trabajadores", "negocio", "trabajadores", "trabajadores", "Trabajadores",
            RotuloPorContexto: c => c.Perfil == PerfilVocabularioTenant.ClienteDirecto ? "Mis trabajadores" : "Trabajadores"),
        new("clientes", "negocio", "clientes", "clientes", "Clientes"),
        new("centros", "negocio", "centros", "centros", "Centros"),
        // Revisión IA, Documentos generados y Plantillas no tienen enlace propio: viven como
        // pestañas de Documentos (REC-062, DEC-28, DDL-080). Sus rutas siguen funcionando para
        // enlaces guardados y notificaciones.
        new("documentos", "negocio", "documentos", "documentos", "Documentos"),
        new("conectar-extension", "negocio", "cuenta/extension", "plataforma", "Conectar extensión"),

        // Comunicaciones es la excepción explícita a "solo lo que ya funciona": se oculta con
        // Comunicaciones:Activo porque no hay ingesta real detrás (ver ComunicacionesOptions).
        new("comunicaciones", "operacion", "comunicaciones", "chat", "Comunicaciones",
            Condicion: c => c.ComunicacionesActivo),
        new("gestiones", "operacion", "gestiones", "evaluaciones", "Gestiones"),
        new("incidencias", "operacion", "incidencias", "incidencias", "Incidencias"),
        new("visitas", "operacion", "visitas", "visitas", "Visitas"),
        // "Vehículos" y no "Vehículos y Maquinaria": Maquinaria es un tramo bloqueado (2.4,
        // pendiente de ADR-006) y un enlace no se adelanta a una entidad que no existe.
        new("vehiculos", "operacion", "vehiculos", "vehiculos", "Vehículos"),
        new("proyectos", "operacion", "proyectos", "proyectos", "Proyectos"),

        // "Mi trabajo" y no "Bandeja": "bandeja" en español significa inbox de correo. Con más de
        // un Tenant autorizado (mismo criterio que el selector de Tenant de la cabecera) es la cola
        // agregada de toda la cartera (/mi-trabajo); con uno solo, /bandeja.
        new("mi-trabajo", "control", "bandeja", "alertas", "Mi trabajo",
            RutaPorContexto: c => c.VariosTenants ? "mi-trabajo" : "bandeja"),
        // Alertas NO es una segunda cola de "qué hacer ahora": desde DEC-4 es la vista agregada de
        // documentación a reclamar, que conserva a propósito EstadoDocumento.Proximo.
        new("alertas", "control", "alertas", "alertas", "Alertas"),
        new("facturacion", "control", "facturacion", "reportes", "Facturación",
            Condicion: c => c.TieneAlgunRol(RolesDeAdministracionAmpliada)),
        new("calendario", "control", "calendario", "calendario", "Calendario"),
        new("reportes", "control", "reportes", "reportes", "Reportes"),

        // Roles, Claves API, Conexiones, Retención, Tipos de documento, Auditoría e Importación
        // viven como paneles de Configuración (DDL-078), su único punto de entrada. Usuarios se
        // queda porque Dirección CAE lo ve y Configuración es solo de Administrador; Verificación
        // en dos pasos apunta al alta personal de 2FA del propio usuario, no a la política.
        new("usuarios", "administracion", "usuarios", "usuarios", "Usuarios"),
        new("configuracion", "administracion", "configuracion", "configuracion", "Configuración",
            Condicion: c => c.TieneAlgunRol(Roles.Administrador)),
        new("verificacion-dos-pasos", "administracion", "cuenta/configurar-2fa", "seguridad", "Verificación en dos pasos",
            Condicion: c => c.TieneAlgunRol(Roles.Administrador)),

        new("delegaciones", "plataforma", "delegaciones", "cartera", "Delegaciones"),
        new("estado-comercial", "plataforma", "configuracion/comercial", "cartera", "Estado comercial"),
        new("conectores-cae", "plataforma", "plataforma/conectores-cae", "plataforma", "Conectores CAE"),
    ];

    /// <summary>Un grupo visible con sus enlaces visibles, en el orden en que se pintan.</summary>
    public sealed record GrupoVisible(GrupoMenuLateral Grupo, IReadOnlyList<EnlaceMenuLateral> Enlaces);

    /// <summary>
    /// Los grupos y enlaces que <paramref name="contexto"/> puede ver, en el orden global guardado
    /// por el Actor de Plataforma TALVEG (o en el del catálogo si no hay ninguno). Un enlace solo
    /// se ve si se ve su grupo y cumple su propia condición: el orden nunca añade nada, solo
    /// recoloca lo que el rol ya permitía ver.
    /// </summary>
    public static IReadOnlyList<GrupoVisible> Visibles(
        ContextoMenuLateral contexto,
        IReadOnlyList<string>? ordenGrupos = null,
        IReadOnlyList<string>? ordenEnlaces = null)
    {
        var enlaces = Reconciliar(Enlaces, e => e.Id, ordenEnlaces);

        return Reconciliar(Grupos, g => g.Id, ordenGrupos)
            .Where(g => g.Visible(contexto))
            .Select(g => new GrupoVisible(g, enlaces
                .Where(e => e.GrupoId == g.Id && (e.Condicion?.Invoke(contexto) ?? true))
                .ToList()))
            .ToList();
    }

    /// <summary>
    /// Reconciliación del orden guardado con el catálogo actual (decisión del 2026-09-23): lo que
    /// está guardado va primero y en ese orden; lo que el catálogo tiene y el orden no nombra
    /// (un grupo o enlace nuevo) va al final, en el orden por defecto; un identificador guardado
    /// que ya no existe se ignora. Sin orden guardado, el catálogo tal cual.
    ///
    /// <para>
    /// Los enlaces se reordenan como una lista plana: como cada enlace pertenece a un único grupo,
    /// su posición relativa dentro del grupo es la que manda, y cambiar de grupo es imposible por
    /// construcción.
    /// </para>
    /// </summary>
    public static IReadOnlyList<T> Reconciliar<T>(
        IReadOnlyList<T> catalogo, Func<T, string> id, IReadOnlyList<string>? orden)
    {
        if (orden is null || orden.Count == 0)
            return catalogo;

        var posicion = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var identificador in orden)
            posicion.TryAdd(identificador, posicion.Count);

        // OrderBy es estable: entre los que no están guardados se conserva el orden del catálogo.
        return catalogo
            .OrderBy(item => posicion.TryGetValue(id(item), out var p) ? p : int.MaxValue)
            .ToList();
    }
}
