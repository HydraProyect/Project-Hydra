using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// La fecha de baja de la que cuentan los 12 meses de conservación de una cuenta, y el interruptor
/// de la purga de auditoría, que nace apagado.
/// </summary>
public class ConservacionTrasLaBajaTests
{
    [Fact]
    public void Una_cuenta_activa_no_tiene_fecha_de_baja() =>
        new ApplicationUser().FechaBajaUtc.Should().BeNull();

    [Fact]
    public void Desactivar_fecha_la_baja()
    {
        var cuenta = new ApplicationUser();

        cuenta.Desactivar();

        cuenta.FechaBajaUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Repetir_la_baja_no_reinicia_el_plazo()
    {
        var cuenta = new ApplicationUser();
        cuenta.Desactivar();
        var primera = cuenta.FechaBajaUtc;

        Thread.Sleep(2);
        cuenta.Desactivar();

        cuenta.FechaBajaUtc.Should().Be(primera);
    }

    [Fact]
    public void Reactivar_anula_la_baja()
    {
        var cuenta = new ApplicationUser();
        cuenta.Desactivar();

        cuenta.Reactivar();

        cuenta.FechaBajaUtc.Should().BeNull();
    }

    [Fact]
    public void La_purga_de_auditoria_nace_apagada() =>
        new RetencionDatosOptions().PurgaRegistrosActiva.Should().BeFalse();
}
