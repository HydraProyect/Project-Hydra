using CaeManager.Domain.Common;
using CaeManager.Web.Features.Auditoria;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public class RangoFechasAuditoriaTests
{
    [Fact]
    public void Por_defecto_arranca_90_dias_antes_del_dia_de_negocio_de_hoy()
    {
        using var reloj = RelojDePrueba.FijarEn(DateTimeOffset.UtcNow);
        RangoFechasAuditoria.DesdePorDefecto().Should().Be(DiaDeNegocio.Hoy().AddDays(-90));
    }

    [Fact]
    public void A_las_23_59_59_9_de_Madrid_las_lecturas_del_rango_ven_el_mismo_dia_con_el_reloj_fijado()
    {
        // 15/07/2026 23:59:59.9 en Madrid (CEST, +02:00): el último décimo antes de la medianoche. Con el reloj del flujo fijado,
        // la lectura del rango y la de la aserción ven el 15/07 aunque entre una y otra pase la medianoche real.
        var instante = new DateTimeOffset(2026, 7, 15, 23, 59, 59, 900, TimeSpan.FromHours(2));
        using var reloj = RelojDePrueba.FijarEn(instante);

        var desde = RangoFechasAuditoria.DesdePorDefecto();

        DiaDeNegocio.Hoy().Should().Be(new DateOnly(2026, 7, 15));
        desde.Should().Be(new DateOnly(2026, 7, 15).AddDays(-90));
    }

    [Fact]
    public void Entre_las_00_00_y_las_02_00_de_Madrid_el_rango_usa_el_dia_de_Madrid_y_no_el_dia_UTC()
    {
        // 2026-07-15T22:30:00Z son las 00:30 del 16/07 en Madrid: el día UTC (15) y el de negocio (16) difieren.
        using var reloj = RelojDePrueba.FijarEn(new DateTimeOffset(2026, 7, 15, 22, 30, 0, TimeSpan.Zero));

        RangoFechasAuditoria.DesdePorDefecto().Should().Be(new DateOnly(2026, 7, 16).AddDays(-90));
    }
    [Theory]
    [InlineData("2026-01-15", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("15/01/2026", false)]
    [InlineData("2026-13-40", false)]
    [InlineData("20260-01-15", false)]
    [InlineData("9999-12-31", false)]
    [InlineData("1800-01-01", false)]
    public void Interpretar_solo_acepta_el_formato_del_campo_de_fecha(string? texto, bool valida)
    {
        RangoFechasAuditoria.Interpretar(texto).HasValue.Should().Be(valida);
    }

    [Fact]
    public void El_dia_se_corta_a_medianoche_peninsular_y_hasta_incluye_el_dia_entero()
    {
        // Enero: Madrid es UTC+1, la medianoche local es 23:00 UTC del día anterior.
        RangoFechasAuditoria.DesdeUtc(new DateOnly(2026, 1, 15)).Should().Be(new DateTime(2026, 1, 14, 23, 0, 0, DateTimeKind.Utc));
        RangoFechasAuditoria.HastaUtcExclusivo(new DateOnly(2026, 1, 15)).Should().Be(new DateTime(2026, 1, 15, 23, 0, 0, DateTimeKind.Utc));
        // Julio: UTC+2.
        RangoFechasAuditoria.DesdeUtc(new DateOnly(2026, 7, 1)).Should().Be(new DateTime(2026, 6, 30, 22, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Sin_fecha_no_hay_limite()
    {
        RangoFechasAuditoria.DesdeUtc(null).Should().BeNull();
        RangoFechasAuditoria.HastaUtcExclusivo(null).Should().BeNull();
    }

    // Hallazgo de la revisión (ronda 2): «hasta el 31/12/9999» no tiene día siguiente; antes AddDays lanzaba y tiraba la
    // pantalla. No hay registro después, así que es lo mismo que no poner límite.
    [Fact]
    public void Hasta_el_ultimo_dia_representable_es_sin_limite_y_no_lanza()
    {
        RangoFechasAuditoria.HastaUtcExclusivo(DateOnly.MaxValue).Should().BeNull();
        RangoFechasAuditoria.HastaUtcExclusivo(DateOnly.MaxValue.AddDays(-1)).Should().NotBeNull("control: el día anterior sí tiene siguiente");
    }
}
