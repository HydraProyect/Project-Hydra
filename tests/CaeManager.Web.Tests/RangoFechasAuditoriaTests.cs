using CaeManager.Domain.Common;
using CaeManager.Web.Features.Auditoria;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public class RangoFechasAuditoriaTests
{
    [Fact]
    public void Por_defecto_arranca_90_dias_antes_del_dia_de_negocio_de_hoy()
    {
        RangoFechasAuditoria.DesdePorDefecto().Should().Be(DiaDeNegocio.Hoy().AddDays(-90));
    }

    [Theory]
    [InlineData("2026-01-15", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("15/01/2026", false)]
    [InlineData("2026-13-40", false)]
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
}
