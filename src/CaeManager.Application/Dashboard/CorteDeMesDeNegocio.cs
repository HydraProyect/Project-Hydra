using CaeManager.Domain.Common;

namespace CaeManager.Application.Dashboard;

/// <summary>
/// Corte de mes de la Facturación y de <see cref="PeriodoKpi"/>. Decisión D-4 (C-12): desde noviembre de 2026
/// el mes empieza a medianoche del día de negocio (<see cref="DiaDeNegocio"/>, Europe/Madrid), como en el Cuadro de
/// dirección (<c>MesCuadroDireccion</c>); los meses anteriores conservan el corte UTC con el que ya se vieron, de modo
/// que no se altera ninguna cifra de un mes cerrado salvo en la frontera: el enlace es continuo, el fin de octubre de 2026 es el inicio de
/// noviembre, así que ninguna hora se cuenta dos veces ni se pierde; el precio es que lo creado el 31 de octubre entre las
/// 23:00 y las 24:00 UTC pasa de octubre a noviembre (la única hora de un mes anterior que cambia de mes).
/// </summary>
public static class CorteDeMesDeNegocio
{
    /// <summary>Primer mes que se corta por el día de negocio.</summary>
    public static readonly DateOnly PrimerMesEnMadrid = new(2026, 11, 1);

    private static readonly DateTime InstanteDeCambio = DiaDeNegocio.InicioEnUtc(PrimerMesEnMadrid);

    /// <summary>Inicio (UTC) de la medianoche de <paramref name="dia"/>: de Madrid desde el primer mes de negocio, UTC antes.</summary>
    public static DateTime InicioDeDiaEnUtc(DateOnly dia) =>
        dia >= PrimerMesEnMadrid
            ? DiaDeNegocio.InicioEnUtc(dia)
            : dia.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>Instante (UTC) en que empieza el mes.</summary>
    public static DateTime InicioEnUtc(int anyo, int mes) => InicioDeDiaEnUtc(new DateOnly(anyo, mes, 1));

    /// <summary>Instante (UTC), exclusivo, en que termina el mes.</summary>
    public static DateTime FinEnUtc(int anyo, int mes) => InicioDeDiaEnUtc(new DateOnly(anyo, mes, 1).AddMonths(1));

    /// <summary>El día al que pertenece un instante UTC según este corte: UTC antes del cambio, de negocio después.</summary>
    public static DateOnly DiaDe(DateTime instanteUtc)
    {
        var utc = DateTime.SpecifyKind(instanteUtc, DateTimeKind.Utc);
        return utc < InstanteDeCambio ? DateOnly.FromDateTime(utc) : DiaDeNegocio.De(utc);
    }

    /// <summary>El mes al que pertenece un instante UTC según este corte.</summary>
    public static (int Anyo, int Mes) MesDe(DateTime instanteUtc)
    {
        var dia = DiaDe(instanteUtc);
        return (dia.Year, dia.Month);
    }
}
