namespace CaeManager.Web.Features.AtajosGlobales;

public record DefinicionAtajo(string Tecla, string Descripcion);

/// <summary>
/// Fuente de verdad única de los atajos globales (Fase D) — tanto para el
/// destino real de "g + letra" como para el texto del chuleta ("?"), así
/// que nunca puedan desincronizarse entre sí.
/// </summary>
public static class CatalogoAtajos
{
    /// <summary>Destinos de "g + letra" — la letra es la que envía atajos-globales.js.</summary>
    public static readonly IReadOnlyDictionary<string, string> DestinosNavegacion = new Dictionary<string, string>
    {
        ["c"] = "/clientes",
        ["e"] = "/empresas",
        ["t"] = "/trabajadores",
        ["d"] = "/documentos",
        ["a"] = "/asignaciones",
        ["b"] = "/bandeja"
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
        new("g c", "Ir a Clientes"),
        new("g e", "Ir a Empresas"),
        new("g t", "Ir a Trabajadores"),
        new("g d", "Ir a Documentos"),
        new("g a", "Ir a Asignaciones"),
        new("g b", "Ir a la Bandeja del gestor")
    ];

    public static readonly IReadOnlyList<DefinicionAtajo> Acciones =
    [
        new("n", "Nuevo aquí (en Clientes, Empresas, Centros, Trabajadores o Documentos)"),
        new("Ctrl/Cmd + K", "Buscador global"),
        new("?", "Mostrar/ocultar esta ayuda")
    ];

    public static readonly IReadOnlyList<DefinicionAtajo> Lista =
    [
        new("j / k", "Fila siguiente / anterior"),
        new("x", "Marcar/desmarcar la fila enfocada"),
        new("Enter", "Abrir la fila enfocada")
    ];
}
