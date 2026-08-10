using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Comunicaciones;

public class SugerenciaVisitaCorreoTests
{
    [Fact]
    public void Crea_una_sugerencia_valida_con_centro_y_fechas()
    {
        var mensajeId = Guid.NewGuid();
        var centroId = Guid.NewGuid();
        var fechaInicio = new DateOnly(2026, 8, 10);
        var fechaFin = new DateOnly(2026, 8, 12);

        var sugerencia = new SugerenciaVisitaCorreo(
            mensajeId, centroId, fechaInicio, fechaFin, "El correo pide entrada de dos trabajadores.", 92, 88, 96);

        sugerencia.MensajeId.Should().Be(mensajeId);
        sugerencia.CentroId.Should().Be(centroId);
        sugerencia.FechaInicioSugerida.Should().Be(fechaInicio);
        sugerencia.FechaFinSugerida.Should().Be(fechaFin);
        sugerencia.Resumen.Should().Be("El correo pide entrada de dos trabajadores.");
        sugerencia.Confianza.Should().Be(92);
        sugerencia.ConfianzaCentro.Should().Be(88);
        sugerencia.ConfianzaFechas.Should().Be(96);
        sugerencia.Resuelta.Should().BeFalse();
    }

    [Fact]
    public void Permite_centro_y_fechas_nulos_cuando_la_IA_no_pudo_resolverlos()
    {
        var sugerencia = new SugerenciaVisitaCorreo(
            Guid.NewGuid(), null, null, null, "Parece una visita pero no se identifica el centro.", 60, 0, 0);

        sugerencia.CentroId.Should().BeNull();
        sugerencia.FechaInicioSugerida.Should().BeNull();
        sugerencia.FechaFinSugerida.Should().BeNull();
    }

    [Fact]
    public void Rechaza_pertenecer_a_un_mensaje_vacio()
    {
        var accion = () => new SugerenciaVisitaCorreo(Guid.Empty, null, null, null, "Resumen", 90, 90, 90);

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rechaza_un_resumen_vacio(string resumenInvalido)
    {
        var accion = () => new SugerenciaVisitaCorreo(Guid.NewGuid(), null, null, null, resumenInvalido, 90, 90, 90);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_una_fecha_de_fin_anterior_a_la_de_inicio()
    {
        var accion = () => new SugerenciaVisitaCorreo(
            Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 8, 12), new DateOnly(2026, 8, 10), "Resumen", 90, 90, 90);

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Rechaza_una_confianza_agregada_fuera_de_rango(int confianzaInvalida)
    {
        var accion = () => new SugerenciaVisitaCorreo(Guid.NewGuid(), null, null, null, "Resumen", confianzaInvalida, 90, 90);

        accion.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Rechaza_una_confianza_de_centro_fuera_de_rango(int confianzaInvalida)
    {
        var accion = () => new SugerenciaVisitaCorreo(Guid.NewGuid(), null, null, null, "Resumen", 90, confianzaInvalida, 90);

        accion.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Rechaza_una_confianza_de_fechas_fuera_de_rango(int confianzaInvalida)
    {
        var accion = () => new SugerenciaVisitaCorreo(Guid.NewGuid(), null, null, null, "Resumen", 90, 90, confianzaInvalida);

        accion.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Resolver_marca_la_sugerencia_como_atendida()
    {
        var sugerencia = new SugerenciaVisitaCorreo(Guid.NewGuid(), null, null, null, "Resumen", 90, 90, 90);

        sugerencia.Resolver();

        sugerencia.Resuelta.Should().BeTrue();
    }
}
