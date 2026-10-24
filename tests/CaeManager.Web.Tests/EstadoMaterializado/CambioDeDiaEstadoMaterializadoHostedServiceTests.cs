using CaeManager.Domain.Common;
using CaeManager.Infrastructure.EstadoMaterializado;
using Xunit;

namespace CaeManager.Web.Tests.EstadoMaterializado;

/// <summary>
/// El cambio de día del estado materializado es a las 00:00 de Madrid, no de UTC (decisión de producto del 2026-10-24):
/// a las 22:00 UTC en horario de verano y a las 23:00 UTC en el de invierno, también los días del cambio de hora de
/// octubre (25 horas) y de marzo (23 horas).
/// </summary>
public class CambioDeDiaEstadoMaterializadoHostedServiceTests
{
    private static DateTimeOffset Utc(int anio, int mes, int dia, int hora, int minuto, int segundo = 0) =>
        new(anio, mes, dia, hora, minuto, segundo, TimeSpan.Zero);

    public static TheoryData<DateTimeOffset, DateOnly> Instantes => new()
    {
        // Verano (UTC+2): la medianoche de Madrid es a las 22:00 UTC del día anterior.
        { Utc(2026, 7, 14, 21, 59, 59), new DateOnly(2026, 7, 14) },
        { Utc(2026, 7, 14, 22, 0), new DateOnly(2026, 7, 15) },
        // Invierno (UTC+1): a las 23:00 UTC.
        { Utc(2026, 12, 14, 22, 58), new DateOnly(2026, 12, 14) },
        { Utc(2026, 12, 14, 23, 0), new DateOnly(2026, 12, 15) },
        // Octubre: el 25 empieza en verano (22:00 UTC del 24) y acaba en invierno (23:00 UTC del 25): dura 25 horas.
        { Utc(2026, 10, 24, 21, 59, 59), new DateOnly(2026, 10, 24) },
        { Utc(2026, 10, 24, 22, 0), new DateOnly(2026, 10, 25) },
        { Utc(2026, 10, 25, 22, 30), new DateOnly(2026, 10, 25) },
        { Utc(2026, 10, 25, 23, 0), new DateOnly(2026, 10, 26) },
        // Marzo: el 29 empieza en invierno (23:00 UTC del 28) y acaba en verano (22:00 UTC del 29): dura 23 horas.
        { Utc(2026, 3, 28, 22, 59, 59), new DateOnly(2026, 3, 28) },
        { Utc(2026, 3, 28, 23, 0), new DateOnly(2026, 3, 29) },
        { Utc(2026, 3, 29, 21, 58), new DateOnly(2026, 3, 29) },
        { Utc(2026, 3, 29, 22, 0), new DateOnly(2026, 3, 30) },
    };

    [Theory]
    [MemberData(nameof(Instantes))]
    public void El_dia_de_negocio_cambia_a_medianoche_de_Madrid(DateTimeOffset instante, DateOnly dia) =>
        Assert.Equal(dia, DiaDeNegocio.De(instante));

    public static TheoryData<DateTimeOffset, DateTimeOffset> AntesDeMedianoche => new()
    {
        // Ahora (23:58 en Madrid) y la medianoche de Madrid siguiente, en UTC.
        { Utc(2026, 7, 14, 21, 58), Utc(2026, 7, 14, 22, 0) },
        { Utc(2026, 12, 14, 22, 58), Utc(2026, 12, 14, 23, 0) },
        { Utc(2026, 10, 24, 21, 58), Utc(2026, 10, 24, 22, 0) },
        { Utc(2026, 10, 25, 22, 58), Utc(2026, 10, 25, 23, 0) },
        { Utc(2026, 3, 28, 22, 58), Utc(2026, 3, 28, 23, 0) },
        { Utc(2026, 3, 29, 21, 58), Utc(2026, 3, 29, 22, 0) },
    };

    [Theory]
    [MemberData(nameof(AntesDeMedianoche))]
    public void La_espera_acaba_en_el_primer_segundo_del_dia_nuevo_de_Madrid(DateTimeOffset ahora, DateTimeOffset medianoche)
    {
        var espera = CambioDeDiaEstadoMaterializadoHostedService.EsperaHastaLaSiguienteComprobacion(ahora);

        Assert.Equal(medianoche - ahora + CambioDeDiaEstadoMaterializadoHostedService.MargenTrasMedianoche, espera);
        Assert.Equal(DiaDeNegocio.De(ahora).AddDays(1), DiaDeNegocio.De(ahora + espera));
    }

    [Theory]
    [InlineData(2026, 7, 14, 10, 0)]
    [InlineData(2026, 10, 25, 1, 30)]   // en plena hora repetida del cambio de octubre
    [InlineData(2026, 3, 29, 1, 30)]    // justo después del salto de marzo
    [InlineData(2026, 10, 24, 22, 0)]   // la propia medianoche: el día siguiente queda a 25 horas
    public void Lejos_de_medianoche_se_comprueba_cada_cinco_minutos(int anio, int mes, int dia, int hora, int minuto) =>
        Assert.Equal(
            CambioDeDiaEstadoMaterializadoHostedService.IntervaloMaximo,
            CambioDeDiaEstadoMaterializadoHostedService.EsperaHastaLaSiguienteComprobacion(Utc(anio, mes, dia, hora, minuto)));

    [Fact]
    public void A_medio_segundo_de_medianoche_la_espera_no_baja_del_margen_y_cae_en_el_dia_nuevo()
    {
        var ahora = Utc(2026, 10, 25, 23, 0) - TimeSpan.FromMilliseconds(500);

        var espera = CambioDeDiaEstadoMaterializadoHostedService.EsperaHastaLaSiguienteComprobacion(ahora);

        Assert.True(espera >= CambioDeDiaEstadoMaterializadoHostedService.MargenTrasMedianoche);
        Assert.Equal(new DateOnly(2026, 10, 26), DiaDeNegocio.De(ahora + espera));
    }
}
