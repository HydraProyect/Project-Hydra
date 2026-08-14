namespace CaeManager.Application.VigilanciaNormativa;

/// <summary>
/// Lista de normas vigiladas del corte mínimo (PLAN_MVP1_FORMATOS.md tramo
/// 1 bis, confirmado 2026-08-14): las cinco normas troncales citadas más a
/// menudo en CATALOGO_FORMATOS_PRL.md, no el censo completo de las 110
/// bases legales del catálogo — ampliar la lista es trabajo de seguimiento,
/// no de este corte. La detección es un `Contains` case-insensitive sobre
/// el título de la publicación: "sin interpretar nada", tal como fija el
/// propio plan.
/// </summary>
public static class NormasVigiladas
{
    public static readonly IReadOnlyList<(string Nombre, string[] Patrones)> Lista =
    [
        ("LPRL", ["Ley 31/1995", "de Prevención de Riesgos Laborales"]),
        ("RSP", ["Real Decreto 39/1997", "Reglamento de los Servicios de Prevención"]),
        ("RD 171/2004", ["Real Decreto 171/2004"]),
        ("RD 1215/1997", ["Real Decreto 1215/1997"]),
        ("RD 773/1997", ["Real Decreto 773/1997"]),
    ];

    /// <summary>Devuelve el nombre de la primera norma vigilada cuyo patrón aparece en el título, o null si ninguna coincide.</summary>
    public static string? Detectar(string titulo)
    {
        foreach (var (nombre, patrones) in Lista)
        {
            if (patrones.Any(patron => titulo.Contains(patron, StringComparison.OrdinalIgnoreCase)))
                return nombre;
        }

        return null;
    }
}
