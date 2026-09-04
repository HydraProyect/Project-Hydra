namespace CaeManager.Web.Features.BusquedaGlobal;

/// <summary>
/// Fuente de verdad del grupo "Ir a" de <see cref="BuscadorGlobal"/> — qué
/// destinos de primer nivel tienen entrada en la paleta global (Ctrl/Cmd+K)
/// y cuáles están exentos a propósito. Vive fuera de <c>BuscadorGlobal.razor.cs</c>
/// (a diferencia del resto de listas privadas de ese componente, p. ej.
/// <c>AccionesFijas</c>) por el mismo motivo que
/// <see cref="CaeManager.Web.Features.AtajosGlobales.CatalogoAtajos"/> es una
/// clase propia y no un campo privado de otro componente: una clase estática
/// normal, no generada desde un <c>.razor</c>, es visible por tipo desde
/// <c>CaeManager.Web.Tests</c> sin <c>InternalsVisibleTo</c> (deliberadamente
/// ausente de este proyecto, ver <c>WebhookWhatsAppEndpoints.EsFirmaValida</c>)
/// y sin leer el código fuente como texto — el trinquete de
/// <c>PaletaCubreDestinosDeTrabajoTests</c> (CaeManager.Web.Tests) referencia
/// <see cref="DestinosNavegacion"/> y <see cref="SegmentosExcluidosDeLaPaleta"/>
/// directamente.
/// </summary>
public static class CoberturaDePaleta
{
    /// <summary>
    /// Grupo "Ir a" del palette (Parte XVI PROMPT 05) — navegación pura a
    /// listados y a páginas de Configuración, nunca acciones que cambien
    /// datos. Filtro simple por substring, sin Query: no son datos, son
    /// rutas fijas de la propia app.
    /// </summary>
    /// <remarks>
    /// HO-006-01 (REC-006) añadió siete entradas: vehículos, proyectos,
    /// visitas, gestiones, incidencias, calendario y comunicaciones existían
    /// como página y en <c>NavMenu.razor</c>, pero el menú era su única vía
    /// — esta lista, a diferencia de
    /// <see cref="CaeManager.Web.Features.AtajosGlobales.CatalogoAtajos.DestinosNavegacion"/>,
    /// no está limitada a una letra por área: admite varias con la misma
    /// inicial ("Ir a Calendario" e "Ir a Comunicaciones" conviven sin
    /// conflicto), así que las siete entraron aquí aunque solo dos
    /// (proyectos, incidencias) tengan además atajo directo "g + letra" —
    /// ver el criterio declarado allí.
    ///
    /// HO-190-01 (REC-190, DEC-75) añadió "Ir a Mi trabajo" (/bandeja) con
    /// la asimetría exactamente inversa a la de REC-006: esa ruta tiene
    /// atajo directo "g b" desde antes de REC-006 pero cero entradas aquí —
    /// quien sabe la tecla llega, quien no, no la encuentra ni buscándola. El
    /// mismo handoff añadió después las quince restantes que DEC-75 nombra
    /// como destinos de trabajo (11) y de administración (5) —las cinco de
    /// administración porque el producto ya había decidido, con
    /// "Configuración" y "Tipos de documento", que la administración SÍ es
    /// un destino buscable— y <see cref="SegmentosExcluidosDeLaPaleta"/>, que
    /// declara las cuatro excepciones de esa acta con su motivo.
    /// </remarks>
    public static readonly IReadOnlyList<(string Nombre, string Ruta)> DestinosNavegacion =
    [
        ("Ir a Clientes", "/clientes"),
        ("Ir a Empresas", "/empresas"),
        ("Ir a Subcontratas", "/subcontratas"),
        ("Ir a Centros", "/centros"),
        ("Ir a Trabajadores", "/trabajadores"),
        ("Ir a Documentos", "/documentos"),
        ("Ir a Dashboard", "/"),
        ("Ir a Configuración", "/configuracion"),
        ("Ir a Claves API", "/configuracion/claves-api"),
        ("Ir a Tipos de documento", "/tipos-documento"),
        ("Ir a Vehículos", "/vehiculos"),
        ("Ir a Proyectos", "/proyectos"),
        ("Ir a Visitas", "/visitas"),
        ("Ir a Gestiones", "/gestiones"),
        ("Ir a Incidencias", "/incidencias"),
        ("Ir a Calendario", "/calendario"),
        ("Ir a Comunicaciones", "/comunicaciones"),
        ("Ir a Mi trabajo", "/bandeja"),

        // HO-190-01 (REC-190, DEC-75) — las once de "destinos de trabajo".
        ("Ir a Alertas", "/alertas"),
        ("Ir a Dashboard Ejecutivo", "/dashboard-ejecutivo"),
        ("Ir a Delegaciones", "/delegaciones"),
        ("Ir a Facturación", "/facturacion"),
        ("Ir a Importación", "/importacion"),
        ("Ir a Integraciones", "/integraciones"),
        ("Ir a Mi firma", "/mi-firma"),
        ("Ir a Plantillas", "/plantillas"),
        ("Ir a Reportes", "/reportes"),
        ("Ir a Visión de cartera", "/vision-cartera"),

        // HO-190-01 (REC-190, DEC-75) — las cinco de administración: mismo
        // criterio que ya aplicaba a Configuración/Tipos de documento.
        ("Ir a Auditoría", "/auditoria"),
        ("Ir a Auditoría IA", "/auditoria-ia"),
        ("Ir a Retención", "/retencion"),
        ("Ir a Roles", "/roles"),
        ("Ir a Usuarios", "/usuarios"),
    ];

    /// <summary>
    /// Excepciones deliberadas a la cobertura de <see cref="DestinosNavegacion"/>.
    /// El criterio de DEC-75 (REC-190), en una frase: <b>la paleta cubre todo
    /// destino al que alguien va a propósito; las excepciones son las
    /// páginas a las que se llega sin quererlo.</b> Cada entrada es un
    /// segmento de primer nivel de una ruta <c>@page</c> (el primer tramo
    /// tras la barra), no un nombre de página.
    /// </summary>
    /// <remarks>
    /// Fuente de verdad para el trinquete de <c>PaletaCubreDestinosDeTrabajoTests</c>
    /// (CaeManager.Web.Tests): una ruta <c>@page</c> de primer nivel nueva
    /// que no esté ni en <see cref="DestinosNavegacion"/> ni aquí pone ese
    /// test en rojo, nombrando la ruta. Ampliar esta lista sin acta de la
    /// Oficina de Reconciliación que la respalde es exactamente el defecto
    /// que HO-006-01 quiso evitar al descartar el trinquete general en su
    /// momento — no añadir una entrada aquí "para que el test pase" sin esa
    /// acta.
    /// <list type="bullet">
    /// <item><description><c>acceso-denegado</c> — se llega sin quererlo (redirección tras un 403), nadie la busca a propósito.</description></item>
    /// <item><description><c>not-found</c> — se llega sin quererlo (404), nadie la busca a propósito.</description></item>
    /// <item><description><c>legal</c> (<c>/legal/privacidad</c>, <c>/legal/terminos</c>) — tiene su puerta natural en el pie de página.</description></item>
    /// <item><description><c>cuenta</c> (<c>/cuenta/...</c>) — tiene su puerta natural en el menú de usuario.</description></item>
    /// </list>
    /// </remarks>
    public static readonly IReadOnlyCollection<string> SegmentosExcluidosDeLaPaleta =
    [
        "acceso-denegado",
        "not-found",
        "legal",
        "cuenta",
    ];
}
