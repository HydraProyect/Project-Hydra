using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

public class CalculadoraEstadoDocumentoTests
{
    private static readonly DateOnly Hoy = new(2026, 7, 15);
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;

    [Fact]
    public void Retorna_NoAplica_cuando_no_hay_fecha_de_vencimiento()
    {
        var estado = CalculadoraEstadoDocumento.Calcular(
            fechaVencimiento: null,
            hoy: Hoy,
            umbralAmbarDias: UmbralAmbarDias,
            umbralRojoDias: UmbralRojoDias);

        estado.Should().Be(EstadoDocumento.SinCaducidad);
    }

    [Fact]
    public void Retorna_Vencido_cuando_la_fecha_de_vencimiento_ya_paso()
    {
        var estado = CalculadoraEstadoDocumento.Calcular(
            fechaVencimiento: Hoy.AddDays(-1),
            hoy: Hoy,
            umbralAmbarDias: UmbralAmbarDias,
            umbralRojoDias: UmbralRojoDias);

        estado.Should().Be(EstadoDocumento.Vencido);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    public void Retorna_Urgente_dentro_del_umbral_rojo(int diasRestantes)
    {
        var estado = CalculadoraEstadoDocumento.Calcular(
            fechaVencimiento: Hoy.AddDays(diasRestantes),
            hoy: Hoy,
            umbralAmbarDias: UmbralAmbarDias,
            umbralRojoDias: UmbralRojoDias);

        estado.Should().Be(EstadoDocumento.Urgente);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(30)]
    public void Retorna_Proximo_entre_el_umbral_rojo_y_el_ambar(int diasRestantes)
    {
        var estado = CalculadoraEstadoDocumento.Calcular(
            fechaVencimiento: Hoy.AddDays(diasRestantes),
            hoy: Hoy,
            umbralAmbarDias: UmbralAmbarDias,
            umbralRojoDias: UmbralRojoDias);

        estado.Should().Be(EstadoDocumento.Proximo);
    }

    [Fact]
    public void Retorna_Vigente_por_encima_del_umbral_ambar()
    {
        var estado = CalculadoraEstadoDocumento.Calcular(
            fechaVencimiento: Hoy.AddDays(31),
            hoy: Hoy,
            umbralAmbarDias: UmbralAmbarDias,
            umbralRojoDias: UmbralRojoDias);

        estado.Should().Be(EstadoDocumento.Vigente);
    }

    [Fact]
    public void Calcula_fecha_de_vencimiento_sumando_los_meses_de_vigencia()
    {
        var vencimiento = CalculadoraEstadoDocumento.CalcularFechaVencimiento(new DateOnly(2026, 3, 3), 12);

        vencimiento.Should().Be(new DateOnly(2027, 3, 3));
    }

    [Fact]
    public void No_calcula_fecha_de_vencimiento_cuando_no_hay_vigencia_en_meses()
    {
        var vencimiento = CalculadoraEstadoDocumento.CalcularFechaVencimiento(new DateOnly(2026, 3, 3), null);

        vencimiento.Should().BeNull();
    }
}
