using CaeManager.Domain.Common;

namespace CaeManager.Application.Common;

/// <summary>
/// Formatea un instante UTC como texto relativo ("hoy", "ayer", "hace N
/// días") para el subtítulo de las acciones en "Recientes" del Command
/// Palette. Vive en Application (no en Web, como sugería el plan original)
/// porque ObtenerRecientesQueryHandler —Application— es quien lo necesita
/// para construir el DTO; Application no puede depender de Web. «Hoy» y
/// «ayer» son días de negocio (<see cref="DiaDeNegocio"/>, Europe/Madrid), sin
/// zona horaria por usuario (no existe esa infraestructura hoy).
/// </summary>
public static class TiempoRelativoTexto
{
    public static string Formatear(DateTime ocurridoEnUtc, DateTime? ahoraUtc = null)
    {
        var ahora = ahoraUtc ?? DateTime.UtcNow;
        var diasTranscurridos = DiaDeNegocio.De(ahora).DayNumber - DiaDeNegocio.De(ocurridoEnUtc).DayNumber;

        return diasTranscurridos switch
        {
            <= 0 => "usada hoy",
            1 => "usada ayer",
            _ => $"usada hace {diasTranscurridos} días"
        };
    }
}
