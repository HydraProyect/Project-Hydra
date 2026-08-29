namespace CaeManager.Application.Common;

/// <summary>
/// Formatea un instante UTC como texto relativo ("hoy", "ayer", "hace N
/// días") para el subtítulo de las acciones en "Recientes" del Command
/// Palette. Vive en Application (no en Web, como sugería el plan original)
/// porque ObtenerRecientesQueryHandler —Application— es quien lo necesita
/// para construir el DTO; Application no puede depender de Web. Calcula
/// siempre sobre UTC, sin zona horaria por usuario (no existe esa
/// infraestructura hoy y no se construye solo para este texto).
/// </summary>
public static class TiempoRelativoTexto
{
    public static string Formatear(DateTime ocurridoEnUtc, DateTime? ahoraUtc = null)
    {
        var ahora = ahoraUtc ?? DateTime.UtcNow;
        var diasTranscurridos = ahora.Date - ocurridoEnUtc.Date;

        return diasTranscurridos.Days switch
        {
            <= 0 => "usada hoy",
            1 => "usada ayer",
            _ => $"usada hace {diasTranscurridos.Days} días"
        };
    }
}
