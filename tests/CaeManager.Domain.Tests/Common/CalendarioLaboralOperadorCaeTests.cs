using CaeManager.Domain.Common;
using FluentAssertions;

namespace CaeManager.Domain.Tests.Common;

public class CalendarioLaboralOperadorCaeTests
{
    [Theory]
    [InlineData("2026-10-19T00:00:00+02:00", "2026-10-20T00:00:00+02:00", 7.5)]
    [InlineData("2026-10-23T00:00:00+02:00", "2026-10-24T00:00:00+02:00", 6.5)]
    [InlineData("2026-07-06T00:00:00+02:00", "2026-07-07T00:00:00+02:00", 6.5)]
    [InlineData("2026-08-03T00:00:00+02:00", "2026-08-04T00:00:00+02:00", 6.5)]
    [InlineData("2026-10-24T00:00:00+02:00", "2026-10-26T00:00:00+01:00", 0)]
    [InlineData("2026-10-19T10:00:00+02:00", "2026-10-19T10:30:00+02:00", 0)]
    [InlineData("2026-10-19T15:00:00+02:00", "2026-10-19T16:00:00+02:00", 0)]
    [InlineData("2026-10-19T09:45:00+02:00", "2026-10-19T10:45:00+02:00", 0.5)]
    [InlineData("2026-10-23T08:00:00+02:00", "2026-10-26T17:00:00+01:00", 14)]
    [InlineData("2026-03-27T08:00:00+01:00", "2026-03-30T17:00:00+02:00", 14)]
    [InlineData("2026-10-19T06:00:00Z", "2026-10-19T15:00:00Z", 7.5)]
    [InlineData("2026-10-19T08:00:00+02:00", "2026-10-19T08:00:00+02:00", 0)]
    public void Cuenta_solo_los_tramos_laborables_en_Madrid(string desde, string hasta, double horas) =>
        new CalendarioLaboralOperadorCae([]).CalcularTiempoLaborable(DateTimeOffset.Parse(desde), DateTimeOffset.Parse(hasta))
            .Should().Be(TimeSpan.FromHours(horas));

    [Fact]
    public void Los_festivos_se_excluyen_y_su_vispera_tiene_jornada_intensiva()
    {
        var festivos = new List<DateOnly> { new(2026, 10, 20) };
        var calendario = new CalendarioLaboralOperadorCae(festivos);
        festivos.Clear(); // La configuración no cambia por mutar la colección de quien llama.

        calendario.CalcularTiempoLaborable(DateTimeOffset.Parse("2026-10-19T00:00:00+02:00"),
            DateTimeOffset.Parse("2026-10-21T00:00:00+02:00")).Should().Be(TimeSpan.FromHours(6.5));
    }

    [Fact]
    public void Rechaza_un_intervalo_invertido()
    {
        var ahora = DateTimeOffset.UtcNow;
        var calcular = () => new CalendarioLaboralOperadorCae([]).CalcularTiempoLaborable(ahora, ahora.AddMinutes(-1));
        calcular.Should().Throw<ArgumentException>();
    }
}
