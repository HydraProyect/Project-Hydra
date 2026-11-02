namespace CaeManager.Application.Dashboard;

/// <summary>
/// Ventana temporal sobre la que se calculan los KPIs que miden actividad (IA,
/// facturación, operativa y fricción). Intervalo <b>semiabierto</b>
/// <c>[Inicio, Fin)</c>: el fin es exclusivo, que es lo que permite encadenar
/// periodos consecutivos sin contar dos veces lo que cae justo en la frontera.
///
/// Cierra el hueco que `Project-Hydra-Negocio/tecnico/ROADMAP.md` arrastraba desde la Fase 63 ("filtro por
/// periodo"): antes cada query calculaba "el mes actual" por su cuenta y no
/// había forma de mirar otro rango.
/// </summary>
public readonly record struct PeriodoKpi(DateTime InicioUtc, DateTime FinUtc)
{
    /// <summary>
    /// Mes natural en curso — el comportamiento que tenía el panel antes de que el periodo fuera elegible, y el valor
    /// por defecto. El mes se corta por el día de negocio (<see cref="CorteDeMesDeNegocio"/>).
    /// </summary>
    public static PeriodoKpi MesActual(DateTime ahoraUtc)
    {
        var (anyo, mes) = CorteDeMesDeNegocio.MesDe(ahoraUtc);
        return DeMeses(new DateOnly(anyo, mes, 1), new DateOnly(anyo, mes, 1));
    }

    /// <summary>Meses naturales completos, del que contiene <paramref name="primerMes"/> al que contiene <paramref name="ultimoMes"/>.</summary>
    public static PeriodoKpi DeMeses(DateOnly primerMes, DateOnly ultimoMes) =>
        new(CorteDeMesDeNegocio.InicioEnUtc(primerMes.Year, primerMes.Month), CorteDeMesDeNegocio.FinEnUtc(ultimoMes.Year, ultimoMes.Month));

    /// <summary>Construye el periodo a partir de dos fechas inclusivas de la interfaz: el día de fin cuenta entero.</summary>
    public static PeriodoKpi DeFechas(DateOnly desde, DateOnly hasta)
    {
        var inicio = CorteDeMesDeNegocio.InicioDeDiaEnUtc(desde);
        var fin = CorteDeMesDeNegocio.InicioDeDiaEnUtc(hasta.AddDays(1));
        return new PeriodoKpi(inicio, fin > inicio ? fin : CorteDeMesDeNegocio.InicioDeDiaEnUtc(desde.AddDays(1)));
    }

    /// <summary>
    /// Meses naturales que el periodo toca, para los KPIs que solo saben calcularse por
    /// mes (la facturación estimada se apoya en <c>ObtenerResumenFacturacionQuery</c>,
    /// que recibe año y mes). Un periodo de dos meses y medio devuelve tres meses: se
    /// prefiere sumar de más y decirlo a inventar un prorrateo que nadie ha decidido.
    /// </summary>
    public IEnumerable<(int Anyo, int Mes)> MesesQueAbarca()
    {
        var (anyo, mes) = CorteDeMesDeNegocio.MesDe(InicioUtc);
        var (ultimoAnyo, ultimoMes) = CorteDeMesDeNegocio.MesDe(FinUtc.AddTicks(-1));

        while (anyo < ultimoAnyo || (anyo == ultimoAnyo && mes <= ultimoMes))
        {
            yield return (anyo, mes);
            var siguiente = new DateOnly(anyo, mes, 1).AddMonths(1);
            (anyo, mes) = (siguiente.Year, siguiente.Month);
        }
    }
}
