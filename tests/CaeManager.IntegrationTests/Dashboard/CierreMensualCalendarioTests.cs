using CaeManager.Infrastructure.Dashboard;
using FluentAssertions;
using Xunit;

namespace CaeManager.IntegrationTests.Dashboard;

public class CierreMensualCalendarioTests
{
    [Theory]
    [InlineData("2026-03-30T12:00:00Z", "2026-03-31T22:00:00Z")]
    [InlineData("2026-10-30T12:00:00Z", "2026-10-31T23:00:00Z")]
    public void El_siguiente_cierre_es_la_medianoche_de_Madrid_con_el_offset_vigente(string ahora, string esperado)
    {
        CierreMensualCuadroDireccionHostedService.ProximoCierreUtc(DateTimeOffset.Parse(ahora).UtcDateTime)
            .Should().Be(DateTimeOffset.Parse(esperado).UtcDateTime);
    }
}
