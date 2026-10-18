namespace CaeManager.Domain.Common;

/// <summary>
/// Calendario puro del Operador CAE, en hora de Madrid. Los festivos son fechas locales explícitas;
/// no se infieren calendarios nacionales, autonómicos ni municipales. La víspera es el día natural anterior.
/// Las pausas y los límites se computan como intervalos semiabiertos, sin redondear minutos.
/// </summary>
public sealed class CalendarioLaboralOperadorCae(IEnumerable<DateOnly> festivos)
{
    private static TimeZoneInfo Madrid => DiaDeNegocio.Zona;
    private readonly HashSet<DateOnly> _festivos = festivos.ToHashSet();

    public DateTimeOffset SumarDiasLaborables(DateTimeOffset inicio, int dias)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dias);
        var local = TimeZoneInfo.ConvertTime(inicio, Madrid);
        var fecha = DateOnly.FromDateTime(local.DateTime);
        for (var pendientes = dias; pendientes > 0;)
        {
            fecha = fecha.AddDays(1);
            if (Tramos(fecha).Any()) pendientes--;
        }
        var hora = TimeOnly.FromDateTime(local.DateTime);
        var tramos = Tramos(fecha).ToArray();
        foreach (var tramo in tramos)
        {
            if (hora < tramo.Inicio) return EnUtc(fecha, tramo.Inicio);
            if (hora < tramo.Fin) return EnUtc(fecha, hora);
        }
        return EnUtc(fecha, tramos[^1].Fin);
    }

    public DateTimeOffset SumarTiempoLaborable(DateTimeOffset inicio, TimeSpan duracion)
    {
        if (duracion <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duracion));
        var fecha = DiaDeNegocio.De(inicio);
        for (; ; fecha = fecha.AddDays(1))
        {
            foreach (var (desde, hasta) in Tramos(fecha))
            {
                var comienzo = EnUtc(fecha, desde);
                if (comienzo < inicio) comienzo = inicio;
                var fin = EnUtc(fecha, hasta);
                if (fin <= comienzo) continue;
                if (fin - comienzo >= duracion) return comienzo + duracion;
                duracion -= fin - comienzo;
            }
        }
    }

    public TimeSpan CalcularTiempoLaborable(DateTimeOffset desde, DateTimeOffset hasta)
    {
        if (hasta < desde)
            throw new ArgumentException("El final no puede preceder al inicio.", nameof(hasta));

        var primera = DiaDeNegocio.De(desde);
        var ultima = DiaDeNegocio.De(hasta);
        var total = TimeSpan.Zero;
        for (var fecha = primera; ; fecha = fecha.AddDays(1))
        {
            foreach (var (inicio, fin) in Tramos(fecha))
            {
                var inicioUtc = EnUtc(fecha, inicio);
                var finUtc = EnUtc(fecha, fin);
                var interseccionDesde = desde > inicioUtc ? desde : inicioUtc;
                var interseccionHasta = hasta < finUtc ? hasta : finUtc;
                if (interseccionHasta > interseccionDesde)
                    total += interseccionHasta - interseccionDesde;
            }

            if (fecha == ultima)
                break;
        }
        return total;
    }

    private IEnumerable<(TimeOnly Inicio, TimeOnly Fin)> Tramos(DateOnly fecha)
    {
        if (fecha.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || _festivos.Contains(fecha))
            yield break;

        yield return (new(8, 0), new(10, 0));
        yield return (new(10, 30), new(15, 0));

        var intensiva = fecha.DayOfWeek == DayOfWeek.Friday || fecha.Month is 7 or 8
            || (fecha != DateOnly.MaxValue && _festivos.Contains(fecha.AddDays(1)));
        if (!intensiva)
            yield return (new(16, 0), new(17, 0));
    }

    private static DateTimeOffset EnUtc(DateOnly fecha, TimeOnly hora) =>
        new(TimeZoneInfo.ConvertTimeToUtc(fecha.ToDateTime(hora, DateTimeKind.Unspecified), Madrid));
}
