namespace CaeManager.Web.Features.Proyectos;

/// <summary>
/// Cuentas de plazo de un proyecto que comparten el panel del listado y la página 360:
/// un solo sitio para que «días abiertos» no dé dos cifras distintas según dónde se mire.
/// </summary>
public static class PlazoProyecto
{
    /// <summary>
    /// Días del periodo abierto del proyecto: de <paramref name="inicio"/> al
    /// cierre real o, si sigue abierto, a <paramref name="hoy"/>. Cuenta
    /// INCLUSIVA —el día de inicio y el de cierre cuentan los dos—, la misma
    /// que usa la facturación por días de proyecto abierto
    /// (ObtenerResumenFacturacionQuery, <c>hasta - desde + 1</c>): dos cifras
    /// de "días abiertos" que no cuadrasen entre sí serían peor que ninguna.
    /// Sin valor si el proyecto todavía no ha empezado.
    /// </summary>
    public static int? DiasAbiertos(DateOnly inicio, DateOnly? cierre, DateOnly hoy)
    {
        var fin = cierre ?? hoy;
        return fin < inicio ? null : fin.DayNumber - inicio.DayNumber + 1;
    }

    /// <summary>
    /// Parte del plazo previsto ya consumida, de 0 a 100, con la misma cuenta inclusiva que
    /// <see cref="DiasAbiertos"/> en el numerador y en el denominador. Sin valor si no hay fin
    /// previsto, si el fin previsto es anterior al inicio o si el proyecto todavía no ha empezado.
    /// </summary>
    public static int? PorcentajeDelPlazo(DateOnly inicio, DateOnly? finPrevisto, DateOnly? cierre, DateOnly hoy)
    {
        if (finPrevisto is not { } fin || fin < inicio || DiasAbiertos(inicio, cierre, hoy) is not { } abiertos)
            return null;

        var total = fin.DayNumber - inicio.DayNumber + 1;
        return Math.Clamp((int)Math.Round(abiertos * 100d / total, MidpointRounding.AwayFromZero), 0, 100);
    }
}
