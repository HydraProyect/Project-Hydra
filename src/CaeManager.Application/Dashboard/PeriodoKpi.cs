namespace CaeManager.Application.Dashboard;

/// <summary>
/// Ventana temporal sobre la que se calculan los KPIs que miden actividad (IA,
/// facturación, operativa y fricción). Intervalo <b>semiabierto</b>
/// <c>[Inicio, Fin)</c>: el fin es exclusivo, que es lo que permite encadenar
/// periodos consecutivos sin contar dos veces lo que cae justo en la frontera.
///
/// Cierra el hueco que `ROADMAP.md` arrastraba desde la Fase 63 ("filtro por
/// periodo"): antes cada query calculaba "el mes actual" por su cuenta y no
/// había forma de mirar otro rango.
/// </summary>
public readonly record struct PeriodoKpi(DateTime InicioUtc, DateTime FinUtc)
{
    /// <summary>Mes natural en curso — el comportamiento que tenía el panel antes de que el periodo fuera elegible, y el valor por defecto.</summary>
    public static PeriodoKpi MesActual(DateTime ahoraUtc)
    {
        var inicio = new DateTime(ahoraUtc.Year, ahoraUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return new PeriodoKpi(inicio, inicio.AddMonths(1));
    }

    /// <summary>Construye el periodo a partir de dos fechas inclusivas de la interfaz: el día de fin cuenta entero.</summary>
    public static PeriodoKpi DeFechas(DateOnly desde, DateOnly hasta)
    {
        var inicio = desde.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var fin = hasta.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return new PeriodoKpi(inicio, fin > inicio ? fin : inicio.AddDays(1));
    }

    /// <summary>
    /// Meses naturales que el periodo toca, para los KPIs que solo saben calcularse por
    /// mes (la facturación estimada se apoya en <c>ObtenerResumenFacturacionQuery</c>,
    /// que recibe año y mes). Un periodo de dos meses y medio devuelve tres meses: se
    /// prefiere sumar de más y decirlo a inventar un prorrateo que nadie ha decidido.
    /// </summary>
    public IEnumerable<(int Anyo, int Mes)> MesesQueAbarca()
    {
        var cursor = new DateTime(InicioUtc.Year, InicioUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var ultimo = FinUtc.AddTicks(-1);

        while (cursor.Year < ultimo.Year || (cursor.Year == ultimo.Year && cursor.Month <= ultimo.Month))
        {
            yield return (cursor.Year, cursor.Month);
            cursor = cursor.AddMonths(1);
        }
    }
}
