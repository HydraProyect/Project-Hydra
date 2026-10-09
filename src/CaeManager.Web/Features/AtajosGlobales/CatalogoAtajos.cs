namespace CaeManager.Web.Features.AtajosGlobales;

/// <summary>
/// Un atajo de la chuleta: la tecla, tal cual se pulsa, y la clave de su
/// descripción en <c>TextosAtajosGlobales</c>.
/// </summary>
public record DefinicionAtajo(string Tecla, string ClaveDescripcion);

/// <summary>
/// Fuente de verdad única de los atajos globales (Fase D) — tanto para el
/// destino real de "g + letra" como para la chuleta ("?"), así que nunca
/// puedan desincronizarse entre sí. El texto de cada atajo vive en
/// <c>TextosAtajosGlobales</c>, referenciado por su clave.
/// </summary>
public static class CatalogoAtajos
{
    /// <summary>
    /// Destinos de "g + letra" — la letra es la que envía atajos-globales.js
    /// (su array <c>TECLAS_DESTINO</c> debe llevar exactamente estas mismas
    /// claves; <c>CatalogoAtajosSincronizadoConJsTests</c>, en
    /// CaeManager.Web.Tests, lo vigila leyendo el fichero, porque el JS no
    /// puede importar este enum y viceversa).
    /// </summary>
    /// <remarks>
    /// Criterio de asignación (REC-006/HO-006-01): una letra directa solo se
    /// da cuando su inicial es inequívoca — no coincide con ninguna letra ya
    /// tomada (navegación o acción) ni con la inicial de otra área de la
    /// misma tanda. Cuando dos áreas comparten la inicial obvia, NINGUNA la
    /// recibe — elegir una arbitrariamente sería justo la letra que nadie
    /// adivina. Por eso, de las siete áreas nuevas de HO-006-01 (vehículos,
    /// proyectos, visitas, gestiones, incidencias, calendario,
    /// comunicaciones), solo dos entraron entonces; D-28 del recorrido en vivo
    /// del 2026-10-01 añadió <c>v</c> (visitas, el área de uso diario del Gestor
    /// CAE; vehículos sigue sin letra) y <c>m</c> (Mi trabajo):
    /// <list type="bullet">
    /// <item><description><c>p</c> → proyectos: inicial libre, sin colisión.</description></item>
    /// <item><description><c>i</c> → incidencias: inicial libre, sin colisión.</description></item>
    /// <item><description>vehículos/visitas comparten "v": la recibe visitas por uso diario; vehículos queda sin letra directa.</description></item>
    /// <item><description><c>m</c> → Mi trabajo: inicial libre (macros no tiene atajo).</description></item>
    /// <item><description>calendario/comunicaciones comparten "c" con clientes (ya tomada) y entre sí — ninguna recibe letra.</description></item>
    /// <item><description>gestiones no puede usar "g": es el propio prefijo de este mecanismo, no una tecla de destino.</description></item>
    /// </list>
    /// Las cinco sin letra directa siguen alcanzables por el grupo "Ir a" de
    /// la paleta global (Ctrl/Cmd+K, <c>BuscadorGlobal.razor.cs</c>), que sí
    /// admite varias áreas con la misma inicial porque no depende de una
    /// sola tecla.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> DestinosNavegacion = new Dictionary<string, string>
    {
        ["c"] = "/clientes",
        ["e"] = "/empresas",
        ["t"] = "/trabajadores",
        ["d"] = "/documentos",
        // Asignaciones ya no es una página aparte — el acordeón de /centros
        // la absorbió (Centro 360, Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.1).
        ["a"] = "/centros",
        ["b"] = "/bandeja",
        ["p"] = "/proyectos",
        ["i"] = "/incidencias",
        ["v"] = "/visitas",
        ["m"] = "/mi-trabajo"
    };

    /// <summary>
    /// Bases de ruta donde "n" (nuevo aquí) tiene sentido — las páginas que
    /// ya soportan <c>?accion=crear</c> (mismo mecanismo que usa el palette
    /// ⌘K). Fuera de estas, "n" no hace nada: no hay una acción de creación
    /// genérica en, por ejemplo, el Dashboard.
    /// </summary>
    public static readonly IReadOnlyCollection<string> BasesConCreacionRapida =
    [
        "clientes", "empresas", "centros", "trabajadores", "documentos"
    ];

    public static readonly IReadOnlyList<DefinicionAtajo> Navegacion =
    [
        new("g c", "IrAClientes"),
        new("g e", "IrAEmpresas"),
        new("g t", "IrATrabajadores"),
        new("g d", "IrADocumentos"),
        new("g a", "IrACentros"),
        new("g b", "IrABandeja"),
        new("g p", "IrAProyectos"),
        new("g i", "IrAIncidencias"),
        new("g v", "IrAVisitas"),
        new("g m", "IrAMiTrabajo")
    ];

    public static readonly IReadOnlyList<DefinicionAtajo> Acciones =
    [
        new("n", "AccionNuevoAqui"),
        new("Ctrl/Cmd + K", "AccionBuscadorGlobal"),
        new("?", "AccionMostrarAyuda")
    ];

    /// <summary>
    /// Atajos dentro de una lista: las teclas que reparte <c>atajos-lista.js</c> (su array
    /// <c>TECLAS_ADMITIDAS</c> lleva exactamente estas; <c>CatalogoAtajosSincronizadoConJsTests</c>
    /// lo vigila). <c>e</c> abre la vista rápida de la fila enfocada ya en edición (el lápiz de
    /// la cabecera del panel); no choca con <c>g e</c>, que <c>atajos-lista.js</c> deja pasar.
    /// <c>f</c> enfoca el buscador «Filtrar esta pantalla» del listado; no choca
    /// con Ctrl/Cmd+K (buscador universal de la cabecera). La búsqueda del menú lateral ya no tiene atajo
    /// de teclado: se abre con su lupa.
    /// </summary>
    public static readonly IReadOnlyList<DefinicionAtajo> Lista =
    [
        new("j / k", "ListaFilaSiguienteAnterior"),
        new("x", "ListaMarcarFila"),
        new("Enter", "ListaAbrirFila"),
        new("e", "ListaEditarFila"),
        new("f", "ListaFiltrarPantalla")
    ];

    /// <summary>
    /// Gestos del modo KeyTips (<c>keytips.js</c>): Alt pulsada y soltada sola lo enciende;
    /// con el modo encendido cada control declarado enseña una letra.
    /// </summary>
    public static readonly IReadOnlyList<DefinicionAtajo> KeyTipsGestos =
    [
        new("Alt", "KeyTipsEncender"),
        new("A – Z", "KeyTipsLetra"),
        new("Retroceso", "KeyTipsSubir"),
        new("Esc", "KeyTipsSalir")
    ];

    /// <summary>
    /// Letras estables de KeyTips: las de los controles compartidos de los listados, iguales
    /// en todas las pantallas. Una letra por control, sin repetir. <c>keytips.js</c> lleva las
    /// mismas en <c>LETRAS_ESTABLES</c> y nunca las da a un control que deduce su letra
    /// (pastillas de filtro, pestañas, opciones de un menú), aunque el control estable no esté
    /// en la pantalla; <c>CatalogoAtajosSincronizadoConJsTests</c> vigila el emparejamiento y
    /// que cada <c>data-keytip</c> del código salga de aquí.
    /// </summary>
    /// <remarks>
    /// <c>X</c> (exportar) y <c>T</c> (franja de estado) están reservadas por la propuesta de
    /// listados: hoy «Exportar a Excel» vive dentro del menú «⋯» (<c>M</c>) y la franja de
    /// estado no existe todavía. <c>G</c> (filtros guardados) vive dentro de «Más filtros»
    /// (<c>L</c>) en el modo pastillas.
    /// </remarks>
    public static readonly IReadOnlyList<DefinicionAtajo> KeyTips =
    [
        new("S", "KeyTipSeleccionar"),
        new("X", "KeyTipExportar"),
        new("K", "KeyTipAtajos"),
        new("M", "KeyTipMasAcciones"),
        new("N", "KeyTipNuevo"),
        new("F", "KeyTipFiltrar"),
        new("T", "KeyTipFranjaEstado"),
        new("L", "KeyTipMasFiltros"),
        new("G", "KeyTipFiltrosGuardados"),
        new("A", "KeyTipAgrupar"),
        new("E", "KeyTipExpandir")
    ];

    /// <summary>
    /// Letras de <see cref="KeyTips"/> que hoy tiene algún control (las que enseña la chuleta).
    /// Las reservadas sin control no se anuncian: una letra anunciada que no hace nada es peor
    /// que una que falta.
    /// </summary>
    public static readonly IReadOnlyCollection<string> KeyTipsConControl = ["S", "K", "M", "N", "F", "L", "A", "E"];
}
