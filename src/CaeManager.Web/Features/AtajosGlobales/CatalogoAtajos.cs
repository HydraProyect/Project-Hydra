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
    /// comunicaciones), solo dos entran aquí:
    /// <list type="bullet">
    /// <item><description><c>p</c> → proyectos: inicial libre, sin colisión.</description></item>
    /// <item><description><c>i</c> → incidencias: inicial libre, sin colisión.</description></item>
    /// <item><description>vehículos/visitas comparten "v" — ninguna de las dos recibe letra.</description></item>
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
        // la absorbió (Centro 360, PLAN-EJECUCION-UX.md § 0.1).
        ["a"] = "/centros",
        ["b"] = "/bandeja",
        ["p"] = "/proyectos",
        ["i"] = "/incidencias"
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
        new("g i", "IrAIncidencias")
    ];

    public static readonly IReadOnlyList<DefinicionAtajo> Acciones =
    [
        new("n", "AccionNuevoAqui"),
        new("Ctrl/Cmd + K", "AccionBuscadorGlobal"),
        new("?", "AccionMostrarAyuda")
    ];

    public static readonly IReadOnlyList<DefinicionAtajo> Lista =
    [
        new("j / k", "ListaFilaSiguienteAnterior"),
        new("x", "ListaMarcarFila"),
        new("Enter", "ListaAbrirFila")
    ];
}
