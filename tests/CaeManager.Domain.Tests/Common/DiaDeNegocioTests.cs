using CaeManager.Domain.Blindaje42;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Common;

/// <summary>
/// El día de negocio es el de la España peninsular (Europe/Madrid, decisión de
/// producto 2026-09-28), no el día UTC ni el de la zona del servidor. Los casos
/// caen en la franja donde los dos difieren: de 22:00 a 24:00 UTC en verano y de
/// 23:00 a 24:00 UTC en invierno, más los dos cambios de hora de 2026 (29 de
/// marzo y 25 de octubre, ambos a la 01:00 UTC).
/// </summary>
public class DiaDeNegocioTests
{
    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;

        // Como en los contenedores: si el día saliera de la zona local, estos tests lo verían.
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static DateTimeOffset Utc(int anyo, int mes, int dia, int hora, int minuto, int segundo = 0) =>
        new(anyo, mes, dia, hora, minuto, segundo, TimeSpan.Zero);

    public static TheoryData<DateTimeOffset, DateOnly> Fronteras => new()
    {
        // Verano (UTC+2): 22:30Z ya es el día siguiente en Madrid.
        { Utc(2026, 7, 15, 21, 59, 59), new DateOnly(2026, 7, 15) },
        { Utc(2026, 7, 15, 22, 0), new DateOnly(2026, 7, 16) },
        { Utc(2026, 7, 15, 22, 30), new DateOnly(2026, 7, 16) },
        // Invierno (UTC+1): 23:30Z ya es el día siguiente en Madrid.
        { Utc(2026, 1, 14, 22, 59, 59), new DateOnly(2026, 1, 14) },
        { Utc(2026, 1, 14, 23, 30), new DateOnly(2026, 1, 15) },
        // Víspera del cambio de marzo: todavía UTC+1.
        { Utc(2026, 3, 28, 22, 59, 59), new DateOnly(2026, 3, 28) },
        { Utc(2026, 3, 28, 23, 0), new DateOnly(2026, 3, 29) },
        // Día del cambio de marzo: a su medianoche ya es UTC+2.
        { Utc(2026, 3, 29, 21, 59, 59), new DateOnly(2026, 3, 29) },
        { Utc(2026, 3, 29, 22, 0), new DateOnly(2026, 3, 30) },
        // Víspera del cambio de octubre: todavía UTC+2.
        { Utc(2026, 10, 24, 21, 59, 59), new DateOnly(2026, 10, 24) },
        { Utc(2026, 10, 24, 22, 0), new DateOnly(2026, 10, 25) },
        // Día del cambio de octubre: a su medianoche ya es UTC+1.
        { Utc(2026, 10, 25, 22, 59, 59), new DateOnly(2026, 10, 25) },
        { Utc(2026, 10, 25, 23, 0), new DateOnly(2026, 10, 26) },
        // Fin de año en invierno.
        { Utc(2026, 12, 31, 23, 30), new DateOnly(2027, 1, 1) },
    };

    [Theory]
    [MemberData(nameof(Fronteras))]
    public void El_dia_cambia_a_la_medianoche_de_Madrid(DateTimeOffset instante, DateOnly esperado)
    {
        DiaDeNegocio.De(instante).Should().Be(esperado);
        DiaDeNegocio.De(instante.UtcDateTime).Should().Be(esperado);
        DiaDeNegocio.Hoy(new RelojFijo(instante)).Should().Be(esperado);

        using var _ = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(instante));
        DiaDeNegocio.Hoy().Should().Be(esperado);
    }

    [Fact]
    public void Control_del_instrumento_los_casos_de_frontera_caen_donde_UTC_y_Madrid_difieren()
    {
        // Si todos los casos dieran el mismo día que UTC, la teoría de arriba no
        // distinguiría Madrid de UTC.
        Fronteras.Count(fila => DateOnly.FromDateTime(((DateTimeOffset)fila[0]).UtcDateTime) != (DateOnly)fila[1])
            .Should().BeGreaterThanOrEqualTo(7);
    }

    [Fact]
    public void La_zona_es_la_peninsular_y_no_la_canaria()
    {
        DiaDeNegocio.Zona.GetUtcOffset(Utc(2026, 1, 14, 12, 0)).Should().Be(TimeSpan.FromHours(1));
        DiaDeNegocio.Zona.GetUtcOffset(Utc(2026, 7, 15, 12, 0)).Should().Be(TimeSpan.FromHours(2));
    }

    [Theory]
    [InlineData(2026, 3, 29, 2026, 3, 28, 23)]
    [InlineData(2026, 3, 30, 2026, 3, 29, 22)]
    [InlineData(2026, 10, 25, 2026, 10, 24, 22)]
    [InlineData(2026, 10, 26, 2026, 10, 25, 23)]
    public void InicioEnUtc_es_la_medianoche_de_Madrid(int a, int m, int d, int au, int mu, int du, int hu)
    {
        var inicio = DiaDeNegocio.InicioEnUtc(new DateOnly(a, m, d));

        inicio.Should().Be(new DateTime(au, mu, du, hu, 0, 0, DateTimeKind.Utc));
        inicio.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Un_instante_en_hora_local_del_servidor_se_rechaza()
    {
        var accion = () => DiaDeNegocio.De(new DateTime(2026, 7, 15, 22, 30, 0, DateTimeKind.Local));

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Un_instante_sin_tipo_se_lee_como_UTC_como_lo_devuelve_EF()
    {
        DiaDeNegocio.De(new DateTime(2026, 7, 15, 22, 30, 0, DateTimeKind.Unspecified))
            .Should().Be(new DateOnly(2026, 7, 16));
    }

    [Fact]
    public void EnHoraPeninsular_convierte_el_instante_a_la_hora_de_Madrid()
    {
        DiaDeNegocio.EnHoraPeninsular(new DateTime(2026, 7, 15, 22, 30, 0, DateTimeKind.Utc))
            .Should().Be(new DateTime(2026, 7, 16, 0, 30, 0));
        DiaDeNegocio.EnHoraPeninsular(new DateTime(2026, 1, 14, 23, 30, 0, DateTimeKind.Utc))
            .Should().Be(new DateTime(2026, 1, 15, 0, 30, 0));
    }

    [Fact]
    public void El_reloj_del_flujo_se_restaura_al_liberarlo()
    {
        var sinFijar = DiaDeNegocio.Hoy();

        using (DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(Utc(2001, 1, 1, 12, 0))))
            DiaDeNegocio.Hoy().Should().Be(new DateOnly(2001, 1, 1));

        DiaDeNegocio.Hoy().Should().BeOnOrAfter(sinFijar);
        DiaDeNegocio.Hoy().Year.Should().BeGreaterThan(2001);
    }

    // ---------------------------------------------------------------------------
    // Reglas «no puede ser futura» del dominio, en la franja donde Madrid ya es mañana.

    private static readonly DateTimeOffset MediaHoraTrasLaMedianocheDeMadridEnVerano = Utc(2026, 7, 15, 22, 30);
    private static readonly DateOnly HoyEnMadrid = new(2026, 7, 16);

    [Fact]
    public void Un_documento_emitido_hoy_en_Madrid_no_es_futuro_aunque_en_UTC_sea_manana()
    {
        using var _ = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(MediaHoraTrasLaMedianocheDeMadridEnVerano));
        var documento = Documento.DeEmpresa(Guid.NewGuid(), Guid.NewGuid(), HoyEnMadrid.AddDays(-30), VigenciaDocumento.NoCaduca);

        var renovar = () => documento.Renovar(HoyEnMadrid, VigenciaDocumento.NoCaduca);
        renovar.Should().NotThrow();

        var renovarManana = () => documento.Renovar(HoyEnMadrid.AddDays(1), VigenciaDocumento.NoCaduca);
        renovarManana.Should().Throw<ArgumentException>().WithMessage("*futura*");
    }

    [Fact]
    public void Una_solicitud_TGSS_de_hoy_en_Madrid_se_admite_y_la_de_manana_no()
    {
        using var _ = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(MediaHoraTrasLaMedianocheDeMadridEnVerano));

        var solicitud = new SolicitudCertificacionTgss(Guid.NewGuid(), Guid.NewGuid(), HoyEnMadrid, Guid.NewGuid());
        var respuesta = () => solicitud.RegistrarRespuesta(ResultadoCertificacionTgss.SinDescubiertos, HoyEnMadrid, Guid.NewGuid());
        respuesta.Should().NotThrow();

        var manana = () => new SolicitudCertificacionTgss(Guid.NewGuid(), Guid.NewGuid(), HoyEnMadrid.AddDays(1), Guid.NewGuid());
        manana.Should().Throw<ArgumentException>().WithMessage("*futura*");
    }

    [Fact]
    public void Una_verificacion_externa_de_hoy_en_Madrid_se_admite_y_la_de_manana_no()
    {
        using var _ = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(MediaHoraTrasLaMedianocheDeMadridEnVerano));

        VerificacionExternaSubcontrata Crear(DateOnly fecha) => new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), fecha, ResultadoVerificacionExterna.Valido, Guid.NewGuid());

        ((Action)(() => Crear(HoyEnMadrid))).Should().NotThrow();
        ((Action)(() => Crear(HoyEnMadrid.AddDays(1)))).Should().Throw<ArgumentException>().WithMessage("*futura*");
    }
}
