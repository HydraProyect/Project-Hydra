using System.Security.Claims;
using CaeManager.Application.VistaDemo;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.IncorporacionCartera.Recursos;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// Lo que el menú lateral necesita saber de quien lo mira para decidir qué enlaces existen. No
/// es autoridad: cada pantalla y cada comando se autorizan por su cuenta (ver
/// <see cref="MenuPorVista"/>); esto solo decide qué pestañas se pintan.
/// </summary>
/// <param name="ParticipaEnIncorporacionCartera">Gestor o Coordinador CAE en su tenant de origen
/// (ParticipaEnIncorporacionCarteraQuery). No sale de <see cref="Usuario"/>: dentro de un Workspace
/// operativo derivado su claim de rol es el de la cartera en ese Tenant propietario.</param>
/// <param name="RotulosPrimeraPersona">«Mi empresa» / «Mis trabajadores» en vez de «Empresas» /
/// «Trabajadores» (UsaRotulosPrimeraPersonaQuery: perfil Cliente Directo y usuario del propio Tenant
/// propietario). El perfil solo no basta: un Operador CAE externo dentro de un Tenant beneficiario
/// ve una lista ajena.</param>
public sealed record ContextoMenuLateral(
    ClaimsPrincipal Usuario,
    VistaDemo? Vista,
    bool ComunicacionesActivo,
    bool EsAdministradorPlataforma,
    PerfilVocabularioTenant Perfil,
    bool VariosTenants,
    bool ParticipaEnIncorporacionCartera = false,
    bool RotulosPrimeraPersona = false)
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

/// <summary>Un enlace del menú lateral. Pertenece a un único grupo, el que fija el catálogo: un orden guardado no puede cambiarlo.</summary>
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
/// Una opción DENTRO de la página de un enlace del menú (una sección de Configuración, una pestaña de
/// Documentos): no es un enlace del menú, solo lo que encuentra la lupa de la cabecera. Solo cabe aquí
/// lo que se puede direccionar con una URL (ruta o <c>?pestana=</c>) y cuya autorización es exactamente
/// la del enlace padre o una regla que el catálogo ya conoce: una subopción nunca concede nada.
/// </summary>
/// <param name="Id">Identificador estable.</param>
/// <param name="EnlaceId">Enlace padre del catálogo: la subopción solo existe si ese enlace es visible para quien mira.</param>
/// <param name="Ruta">Ruta relativa con su parámetro si lo lleva (<c>documentos?pestana=plantillas</c>).</param>
/// <param name="ClaveRotulo">Clave del rótulo en los recursos localizados (nunca un literal: el menú se pinta en cada idioma).</param>
/// <param name="Fuente">Recurso de donde sale el rótulo: el hub de Configuración para sus secciones (una sola fuente), o TextosComunes.</param>
/// <param name="Condicion">Restricción propia además de la del padre; null si basta con ver el padre.</param>
public sealed record SubopcionMenuLateral(
    string Id,
    string EnlaceId,
    string Ruta,
    string ClaveRotulo,
    FuenteRotuloSubopcion Fuente,
    Func<ContextoMenuLateral, bool>? Condicion = null);

/// <summary>Recurso localizado del que sale el rótulo de una <see cref="SubopcionMenuLateral"/>.</summary>
public enum FuenteRotuloSubopcion
{
    TextosComunes,
    TextosConfiguracion,
}

/// <summary>
/// Catálogo del menú lateral de los usuarios internos: cada grupo y cada enlace con su
/// identificador estable, su regla de visibilidad y su posición por defecto (el orden de las
/// listas). El menú del rol Cliente NO está aquí: es deliberadamente distinto y mínimo (Fase 31),
/// fijo, y vive como marcado en <c>NavMenu.razor</c>.
///
/// <para>
/// Solo se listan los módulos que ya existen y funcionan (ver Project-Hydra-Negocio/tecnico/ROADMAP.md): un enlace a una
/// pantalla que todavía no existe es peor que no tener el enlace. Los datos que ve cada enlace ya
/// quedan acotados por IAlcanceDatosService en las Queries — este catálogo solo decide qué
/// pestañas existen, no qué filas aparecen dentro. Un único set de iconos outline (ver
/// Project-Hydra-Negocio/tecnico/docs/archive/design/DESIGN_SYSTEM.md, "Iconografía").
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
    /// riesgo" — al migrar esa pieza a Dashboard Ejecutivo (Project-Hydra-Negocio/tecnico/docs/blueprints/OPERATIONAL-HOME.md
    /// § 7) no debía perder esa visibilidad. No se añade Consulta al rol de administración
    /// ampliada porque ese también gatea Facturación y Administración, que Consulta no debe ver.
    /// </summary>
    private const string RolesDeDashboardEjecutivo = $"{Roles.Administrador},{Roles.DireccionCae},{Roles.Consulta}";

    private const string RolesDeCartera = $"{Roles.Administrador},{Roles.DireccionCae},{Roles.CoordinadorCae}";

    /// <summary>
    /// Quién ve las pestañas de gestión documental de Documentos. Mismo valor que
    /// <c>Documentos.RolesDeGestionDocumental</c> (lo vigila un test): la pestaña se pinta a todos,
    /// pero su contenido solo a estos roles, y el menú no debe ofrecer un destino que la página niega.
    /// </summary>
    private const string RolesDeGestionDocumental =
        $"{Roles.Administrador},{Roles.DireccionCae},{Roles.CoordinadorCae},{Roles.GestorCae}";

    /// <summary>
    /// Quién tiene «Mi trabajo». Mismo valor que el <c>[Authorize]</c> de <c>Bandeja</c> y de
    /// <c>MiTrabajo</c> (lo vigila un test): el rol Consulta ve Operación, pero esas dos páginas le
    /// deniegan el acceso, y el menú no debe ofrecer un destino que la página niega. Público porque
    /// quien enlace a Mi trabajo desde otra pantalla decide con esto si ofrece el enlace.
    /// </summary>
    public const string RolesDeMiTrabajo =
        $"{Roles.Administrador},{Roles.DireccionCae},{Roles.CoordinadorCae},{Roles.GestorCae}";

    /// <summary>
    /// Adónde lleva «Mi trabajo»: con más de un Tenant autorizado, a la cola agregada de toda la
    /// cartera (<c>/mi-trabajo</c>); con uno solo, a <c>/bandeja</c>. Una sola regla para el menú y
    /// para cualquier pantalla que enlace allí, que así no pueden divergir.
    /// </summary>
    public static string RutaMiTrabajo(bool variosTenants) => variosTenants ? "mi-trabajo" : "bandeja";

    public static IReadOnlyList<GrupoMenuLateral> Grupos { get; } =
    [
        // Dashboard / Visión de cartera / Dashboard Ejecutivo son tres Operational Home distintos
        // (DDL-004 § 1.1), con conjuntos de roles no anidados (Consulta ve Dashboard+Ejecutivo pero
        // no Cartera; CoordinadorCae ve Dashboard+Cartera pero no Ejecutivo) — no se pliegan en
        // pestañas de una sola pantalla porque Pestanas no autoriza por pestaña hoy y sus layouts
        // son incompatibles. Agruparlos solo bajo un mismo encabezado visual "Dashboards" (informe
        // de consolidación de menú, 2026-09-01 § 6) deja esa pregunta de producto abierta.
        new("dashboards", "Dashboards", AbiertoPorDefecto: true, c => c.TieneAlgunRol(RolesConMenuCompleto)),

        // Reparto y orden por mandato explícito del propietario (2026-09-29), que sustituye al de
        // DDL-073 (cadena de titularidad, Documentos al final). Negocio: Empresas, Trabajadores,
        // Documentos, Clientes, Centros, Vehículos, Proyectos y Subcontratas, en ese orden.
        new("negocio", "Negocio", AbiertoPorDefecto: true, c => c.TieneAlgunRol(RolesConMenuCompleto)),

        // Mismo mandato. Operación: Mi trabajo, Comunicaciones, Gestiones, Visitas e Incidencias, en
        // ese orden; Vehículos y Proyectos pasaron a Negocio y Mi trabajo llegó desde Control.
        new("operacion", "Operación", AbiertoPorDefecto: true, c => c.TieneAlgunRol(RolesConMenuCompleto)),

        // Colapsado por defecto (informe de consolidación de menú, 2026-09-01 § 6): Control: Alertas,
        // Facturación, Calendario y Reportes, y por el mandato del propietario del 2026-09-29
        // cierra con Conectar extensión (Mi trabajo salió a Operación).
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
        new("dashboard", "dashboards", "", "inicio", "Inicio", CoincidenciaExacta: true),
        new("vision-cartera", "dashboards", "vision-cartera", "cartera", "Visión de cartera",
            Condicion: c => c.TieneAlgunRol(RolesDeCartera)),
        // Rótulo localizado (TextosIncorporacionCartera), a diferencia de sus vecinos todavía literales:
        // lo da RotuloPorContexto, así que el Rotulo fijo queda vacío.
        new("solicitudes-cartera", "dashboards", "cartera/solicitudes", "solicitud", "",
            // Por el rol en el tenant de origen, no por IsInRole (ver ParticipaEnIncorporacionCartera).
            Condicion: c => c.ParticipaEnIncorporacionCartera,
            RotuloPorContexto: _ => TextosIncorporacionCartera.Texto("EnlaceMenu")),
        new("dashboard-ejecutivo", "dashboards", "dashboard-ejecutivo", "ejecutivo", "Dashboard Ejecutivo",
            Condicion: c => c.TieneAlgunRol(RolesDeDashboardEjecutivo)),

        // DDL-072 (decisión del propietario 2026-09-28): "Mi empresa" (registro único) solo si el
        // perfil es Cliente Directo Y el usuario es del propio Tenant propietario; "Empresas" (lista)
        // en cualquier otro caso. La condición vive en UsaRotulosPrimeraPersonaQuery, la misma que
        // usan los títulos de página; aquí solo se consume como RotulosPrimeraPersona.
        new("empresas", "negocio", "empresas", "empresas", "Empresas",
            RotuloPorContexto: c => c.RotulosPrimeraPersona ? "Mi empresa" : "Empresas"),
        new("trabajadores", "negocio", "trabajadores", "trabajadores", "Trabajadores",
            RotuloPorContexto: c => c.RotulosPrimeraPersona ? "Mis trabajadores" : "Trabajadores"),
        // Revisión IA, Documentos generados y Plantillas no tienen enlace propio: viven como
        // pestañas de Documentos (REC-062, DEC-28, DDL-080). Sus rutas siguen funcionando para
        // enlaces guardados y notificaciones.
        new("documentos", "negocio", "documentos", "documentos", "Documentos"),
        new("clientes", "negocio", "clientes", "clientes", "Clientes"),
        new("centros", "negocio", "centros", "centros", "Centros"),
        // Vehículos y Proyectos son de Negocio por mandato del propietario del 2026-09-29.
        // "Vehículos" y no "Vehículos y Maquinaria": Maquinaria es un tramo bloqueado
        // (2.4, pendiente de ADR-006) y un enlace no se adelanta a una entidad que no existe.
        new("vehiculos", "negocio", "vehiculos", "vehiculos", "Vehículos"),
        new("proyectos", "negocio", "proyectos", "proyectos", "Proyectos"),
        new("subcontratas", "negocio", "subcontratas", "subcontratas", "Subcontratas"),

        // "Mi trabajo" y no "Bandeja": "bandeja" en español significa inbox de correo. Con más de
        // un Tenant autorizado (mismo criterio que el selector de Tenant de la cabecera) es la cola
        // agregada de toda la cartera (/mi-trabajo); con uno solo, /bandeja. Abre Operación por
        // mandato del propietario del 2026-09-29. No se ofrece al rol Consulta: ninguna de las dos
        // páginas lo autoriza (ver RolesDeMiTrabajo).
        new("mi-trabajo", "operacion", "bandeja", "mi-trabajo", "Mi trabajo",
            Condicion: c => c.TieneAlgunRol(RolesDeMiTrabajo),
            RutaPorContexto: c => RutaMiTrabajo(c.VariosTenants)),
        // Comunicaciones es la excepción explícita a "solo lo que ya funciona": se oculta con
        // Comunicaciones:Activo porque no hay ingesta real detrás (ver ComunicacionesOptions).
        new("comunicaciones", "operacion", "comunicaciones", "chat", "Comunicaciones",
            Condicion: c => c.ComunicacionesActivo),
        new("gestiones", "operacion", "gestiones", "evaluaciones", "Gestiones"),
        new("visitas", "operacion", "visitas", "visitas", "Visitas"),
        new("incidencias", "operacion", "incidencias", "incidencias", "Incidencias"),

        // Alertas NO es una segunda cola de "qué hacer ahora": desde DEC-4 es la vista agregada de
        // documentación a reclamar, que conserva a propósito EstadoDocumento.Proximo.
        new("alertas", "control", "alertas", "alertas", "Alertas"),
        new("facturacion", "control", "facturacion", "facturacion", "Facturación",
            Condicion: c => c.TieneAlgunRol(RolesDeAdministracionAmpliada)),
        new("calendario", "control", "calendario", "calendario", "Calendario"),
        new("reportes", "control", "reportes", "reportes", "Reportes"),
        // Cierra Control por mandato del propietario del 2026-09-29 (salvo el enlace de equipo del Coordinador CAE, de más abajo).
        new("conectar-extension", "control", "cuenta/extension", "conectar", "Conectar extensión"),
        // Rótulo «Mi equipo» (no «Usuarios»): Administración ya tiene su «Usuarios»: así el catálogo no
        // repite el rótulo para la misma ruta. La pantalla sigue titulándose Usuarios.
        // D-12: el Coordinador CAE gestiona su equipo en /usuarios (la página ya lo autoriza por rol,
        // Usuarios.razor) pero no ve el grupo «Administración», que es de Administrador y Dirección CAE.
        // Este enlace solo hace descubrible lo ya autorizado: misma ruta, misma autorización, ningún
        // permiso nuevo. Administrador y Dirección CAE conservan «usuarios» en Administración, y no
        // lo ven duplicado aquí.
        new("usuarios-equipo", "control", "usuarios", "equipo", "Mi equipo",
            Condicion: c => c.TieneAlgunRol(Roles.CoordinadorCae)
                            && !c.TieneAlgunRol(RolesDeAdministracionAmpliada)),

        // Roles, Claves API, Conexiones, Retención, Tipos de documento, Auditoría e Importación
        // viven como paneles de Configuración (DDL-078), su único punto de entrada. Usuarios se
        // queda porque Dirección CAE lo ve y Configuración es solo de Administrador; Verificación
        // en dos pasos apunta al alta personal de 2FA del propio usuario, no a la política.
        new("usuarios", "administracion", "usuarios", "usuarios", "Usuarios"),
        new("configuracion", "administracion", "configuracion", "configuracion", "Configuración",
            Condicion: c => c.TieneAlgunRol(Roles.Administrador)),
        new("verificacion-dos-pasos", "administracion", "cuenta/configurar-2fa", "seguridad", "Verificación en dos pasos",
            Condicion: c => c.TieneAlgunRol(Roles.Administrador)),

        new("delegaciones", "plataforma", "delegaciones", "delegacion", "Delegaciones"),
        new("estado-comercial", "plataforma", "configuracion/comercial", "etiqueta", "Estado comercial"),
        new("conectores-cae", "plataforma", "plataforma/conectores-cae", "plataforma", "Conectores CAE"),
    ];

    /// <summary>
    /// Opciones que la lupa encuentra dentro de las páginas del menú. Criterio de inclusión: tiene URL
    /// propia (ruta o <c>?pestana=</c>) y la misma autorización que su enlace padre o una regla del
    /// catálogo. Quedan FUERA a propósito: las pestañas de Facturación y de los detalles por registro
    /// (Proyectos, Empresas…), que se cambian en la página sin dejar rastro en la URL; y, dentro de
    /// Configuración, las entradas que tienen autoridad propia distinta del rol Administrador del hub
    /// (organización, plataforma, orden del menú y accesos a documentos sensibles).
    /// </summary>
    public static IReadOnlyList<SubopcionMenuLateral> Subopciones { get; } =
    [
        // Configuración: el hub resuelve /configuracion/{id}; los ids y las claves de rótulo son los de
        // Configuracion.razor.cs (lo vigila un test). Visibilidad = la del enlace «configuracion»
        // (rol Administrador), que es la misma [Authorize] del hub.
        new("config-usuarios", "configuracion", "configuracion/usuarios", "EntradaUsuariosNombre", FuenteRotuloSubopcion.TextosConfiguracion),
        new("config-roles", "configuracion", "configuracion/roles", "EntradaRolesNombre", FuenteRotuloSubopcion.TextosConfiguracion),
        new("config-api", "configuracion", "configuracion/api", "EntradaApiNombre", FuenteRotuloSubopcion.TextosConfiguracion),
        new("config-integraciones", "configuracion", "configuracion/integraciones", "EntradaIntegracionesNombre", FuenteRotuloSubopcion.TextosConfiguracion),
        new("config-importar", "configuracion", "configuracion/importar", "EntradaImportarNombre", FuenteRotuloSubopcion.TextosConfiguracion),
        new("config-tipos", "configuracion", "configuracion/tipos", "EntradaTiposNombre", FuenteRotuloSubopcion.TextosConfiguracion),
        new("config-ia", "configuracion", "configuracion/ia", "EntradaIaNombre", FuenteRotuloSubopcion.TextosConfiguracion),
        new("config-macros", "configuracion", "configuracion/macros", "EntradaMacrosNombre", FuenteRotuloSubopcion.TextosConfiguracion,
            // Macros.razor.cs expulsa a /not-found sin Comunicaciones:Activo: mismo gate que el enlace «comunicaciones».
            Condicion: c => c.ComunicacionesActivo),
        new("config-params", "configuracion", "configuracion/params", "EntradaParamsNombre", FuenteRotuloSubopcion.TextosConfiguracion),
        new("config-retencion", "configuracion", "configuracion/retencion", "EntradaRetencionNombre", FuenteRotuloSubopcion.TextosConfiguracion),
        new("config-auditoria", "configuracion", "configuracion/auditoria", "EntradaAuditoriaNombre", FuenteRotuloSubopcion.TextosConfiguracion),
        new("config-auditoria-ia", "configuracion", "configuracion/auditoria-ia", "EntradaAuditoriaIaNombre", FuenteRotuloSubopcion.TextosConfiguracion),
        new("config-automatizaciones", "configuracion", "configuracion/automatizaciones", "EntradaAutomatizacionesNombre", FuenteRotuloSubopcion.TextosConfiguracion),

        // Pestañas de Documentos con deep-link ?pestana= (Documentos.IdDePestanaDeUrl). «Estado» es la
        // propia página. Rótulos de la tira de pestañas de la página.
        new("documentos-plataformas", "documentos", "documentos?pestana=plataforma", "MenuSubDocumentosPlataformas", FuenteRotuloSubopcion.TextosComunes,
            Condicion: c => c.TieneAlgunRol(RolesDeGestionDocumental)),
        new("documentos-reclamaciones", "documentos", "documentos?pestana=reclamaciones", "MenuSubDocumentosReclamaciones", FuenteRotuloSubopcion.TextosComunes,
            Condicion: c => c.TieneAlgunRol(RolesDeGestionDocumental)),
        new("documentos-preventivo", "documentos", "documentos?pestana=sugerencias", "MenuSubDocumentosPreventivo", FuenteRotuloSubopcion.TextosComunes),
        new("documentos-revision-ia", "documentos", "documentos?pestana=revision-ia", "MenuSubDocumentosRevisionIa", FuenteRotuloSubopcion.TextosComunes,
            Condicion: c => c.TieneAlgunRol(RolesDeGestionDocumental)),
        new("documentos-plantillas", "documentos", "documentos?pestana=plantillas", "MenuSubDocumentosPlantillas", FuenteRotuloSubopcion.TextosComunes,
            Condicion: c => c.TieneAlgunRol(RolesDeGestionDocumental)),
    ];

    /// <summary>Una subopción visible con el enlace padre bajo el que se muestra.</summary>
    public sealed record SubopcionVisible(SubopcionMenuLateral Subopcion, EnlaceMenuLateral Enlace);

    /// <summary>
    /// Las subopciones que <paramref name="contexto"/> puede ver: solo las de un enlace que ya está en
    /// <paramref name="visibles"/> (mismos grupo y condición del catálogo) y que cumplen su propia
    /// condición. Nunca añade un destino que el menú no ofrecería ya.
    /// </summary>
    public static IReadOnlyList<SubopcionVisible> SubopcionesVisibles(
        ContextoMenuLateral contexto, IReadOnlyList<GrupoVisible> visibles)
    {
        var enlaces = visibles.SelectMany(g => g.Enlaces).ToDictionary(e => e.Id);
        return Subopciones
            .Where(s => enlaces.ContainsKey(s.EnlaceId) && (s.Condicion?.Invoke(contexto) ?? true))
            .Select(s => new SubopcionVisible(s, enlaces[s.EnlaceId]))
            .ToList();
    }

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
        // Nulos y repetidos no los deja entrar el dominio, pero la fila solo se valida al escribir:
        // una fila tocada por SQL no puede tumbar el menú de todos los Tenants.
        foreach (var identificador in orden)
            if (identificador is not null)
                posicion.TryAdd(identificador, posicion.Count);

        // OrderBy es estable: entre los que no están guardados se conserva el orden del catálogo.
        return catalogo
            .OrderBy(item => posicion.TryGetValue(id(item), out var p) ? p : int.MaxValue)
            .ToList();
    }
}
