using CaeManager.Web.Services;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>Umbral de ausencia y throttle de escritura del resumen (docs/blueprints/OPERATIONAL-HOME.md § 6, DDL-068).</summary>
public class ActividadUsuarioServiceTests
{
    private static readonly DateTime Ahora = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Sin_actividad_previa_no_hay_ausencia_pero_si_se_escribe()
    {
        var (ausente, debeEscribir) = ActividadUsuarioService.Evaluar(anterior: null, Ahora);

        ausente.Should().BeFalse();
        debeEscribir.Should().BeTrue();
    }

    [Fact]
    public void Menos_de_diez_minutos_no_es_ausencia()
    {
        var (ausente, _) = ActividadUsuarioService.Evaluar(Ahora.AddMinutes(-9), Ahora);

        ausente.Should().BeFalse();
    }

    [Fact]
    public void Mas_de_diez_minutos_es_ausencia()
    {
        var (ausente, _) = ActividadUsuarioService.Evaluar(Ahora.AddMinutes(-11), Ahora);

        ausente.Should().BeTrue();
    }

    [Fact]
    public void Dentro_del_minuto_de_throttle_no_se_reescribe()
    {
        var (_, debeEscribir) = ActividadUsuarioService.Evaluar(Ahora.AddSeconds(-30), Ahora);

        debeEscribir.Should().BeFalse();
    }

    [Fact]
    public void Pasado_el_minuto_de_throttle_se_reescribe_aunque_no_haya_ausencia()
    {
        var (ausente, debeEscribir) = ActividadUsuarioService.Evaluar(Ahora.AddMinutes(-2), Ahora);

        ausente.Should().BeFalse();
        debeEscribir.Should().BeTrue();
    }
}
