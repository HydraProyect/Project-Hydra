using CaeManager.Application.Dashboard;
using FluentAssertions;

namespace CaeManager.Application.Tests.Dashboard;

/// <summary>
/// Decisión D-4 (C-12): Facturación y <see cref="PeriodoKpi"/> cortan el mes por el día de negocio en
/// Europe/Madrid, como el Cuadro de dirección, desde noviembre de 2026; los meses anteriores conservan el corte UTC.
/// </summary>
public class CorteDeMesDeNegocioTests
{
    private static DateTime Utc(int a, int m, int d, int h = 0, int min = 0) => new(a, m, d, h, min, 0, DateTimeKind.Utc);

    [Fact]
    public void Noviembre_empieza_a_medianoche_de_Madrid_y_diciembre_tambien()
    {
        CorteDeMesDeNegocio.InicioEnUtc(2026, 11).Should().Be(Utc(2026, 10, 31, 23));
        CorteDeMesDeNegocio.InicioEnUtc(2026, 12).Should().Be(Utc(2026, 11, 30, 23));
        CorteDeMesDeNegocio.InicioEnUtc(2027, 7).Should().Be(Utc(2027, 6, 30, 22));
    }

    [Fact]
    public void Los_meses_anteriores_a_noviembre_de_2026_conservan_el_corte_UTC()
    {
        CorteDeMesDeNegocio.InicioEnUtc(2026, 9).Should().Be(Utc(2026, 9, 1));
        CorteDeMesDeNegocio.InicioEnUtc(2026, 1).Should().Be(Utc(2026, 1, 1));
    }

    [Fact]
    public void El_mes_en_curso_a_las_0030_de_Madrid_del_1_de_noviembre_es_noviembre()
    {
        var periodo = PeriodoKpi.MesActual(Utc(2026, 10, 31, 23, 30));

        periodo.InicioUtc.Should().Be(Utc(2026, 10, 31, 23));
        periodo.FinUtc.Should().Be(Utc(2026, 11, 30, 23));
        periodo.MesesQueAbarca().Should().Equal((2026, 11));
    }

    [Fact]
    public void El_mes_en_curso_a_las_0030_de_Madrid_del_1_de_diciembre_es_diciembre()
    {
        var periodo = PeriodoKpi.MesActual(Utc(2026, 11, 30, 23, 30));

        periodo.InicioUtc.Should().Be(Utc(2026, 11, 30, 23));
        periodo.FinUtc.Should().Be(Utc(2026, 12, 31, 23));
        periodo.MesesQueAbarca().Should().Equal((2026, 12));
    }

    [Fact]
    public void Septiembre_no_cambia_y_octubre_enlaza_con_noviembre_sin_solaparse()
    {
        var septiembre = PeriodoKpi.MesActual(Utc(2026, 9, 15));
        septiembre.InicioUtc.Should().Be(Utc(2026, 9, 1));
        septiembre.FinUtc.Should().Be(Utc(2026, 10, 1));

        var octubre = PeriodoKpi.MesActual(Utc(2026, 10, 15));
        octubre.InicioUtc.Should().Be(Utc(2026, 10, 1));
        octubre.FinUtc.Should().Be(CorteDeMesDeNegocio.InicioEnUtc(2026, 11), "dos meses consecutivos no pueden contar dos veces la misma hora");
    }

    [Fact]
    public void Un_rango_de_fechas_de_la_interfaz_cuenta_dias_de_Madrid_desde_noviembre()
    {
        var periodo = PeriodoKpi.DeFechas(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31));

        periodo.InicioUtc.Should().Be(Utc(2026, 11, 30, 23));
        periodo.FinUtc.Should().Be(Utc(2026, 12, 31, 23));
        periodo.MesesQueAbarca().Should().Equal((2026, 12));
    }

    [Fact]
    public void Un_rango_de_fechas_anterior_conserva_UTC()
    {
        var periodo = PeriodoKpi.DeFechas(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

        periodo.InicioUtc.Should().Be(Utc(2026, 3, 1));
        periodo.FinUtc.Should().Be(Utc(2026, 4, 1));
        periodo.MesesQueAbarca().Should().Equal((2026, 3));
    }
}
