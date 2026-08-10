using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Subcontratas;

public class CalculadoraEstadoSupervisionTests
{
    private static readonly DateOnly Hoy = new(2026, 8, 10);
    private const int UmbralAmbar = 30;
    private const int UmbralRojo = 15;

    private static EstadoSupervision Calcular(ResultadoVerificacionExterna? resultado, DateOnly? validoHasta) =>
        CalculadoraEstadoSupervision.Calcular(resultado, validoHasta, Hoy, UmbralAmbar, UmbralRojo);

    [Fact]
    public void Sin_verificacion_es_sin_verificar() =>
        Calcular(null, null).Should().Be(EstadoSupervision.SinVerificar);

    [Theory]
    [InlineData(ResultadoVerificacionExterna.NoValido)]
    [InlineData(ResultadoVerificacionExterna.NoEncontrado)]
    public void Un_resultado_negativo_es_no_valido_aunque_tenga_fecha_lejana(ResultadoVerificacionExterna resultado) =>
        Calcular(resultado, Hoy.AddDays(365)).Should().Be(EstadoSupervision.NoValido);

    [Fact]
    public void Valido_sin_caducidad_declarada_es_vigente() =>
        Calcular(ResultadoVerificacionExterna.Valido, null).Should().Be(EstadoSupervision.Vigente);

    [Fact]
    public void Valido_fuera_de_umbrales_es_vigente() =>
        Calcular(ResultadoVerificacionExterna.Valido, Hoy.AddDays(UmbralAmbar + 1)).Should().Be(EstadoSupervision.Vigente);

    [Fact]
    public void Valido_dentro_del_umbral_ambar_es_proximo() =>
        Calcular(ResultadoVerificacionExterna.Valido, Hoy.AddDays(UmbralAmbar)).Should().Be(EstadoSupervision.Proximo);

    [Fact]
    public void Valido_dentro_del_umbral_rojo_es_urgente() =>
        Calcular(ResultadoVerificacionExterna.Valido, Hoy.AddDays(UmbralRojo)).Should().Be(EstadoSupervision.Urgente);

    [Fact]
    public void Valido_que_caduca_hoy_es_urgente_no_vencido() =>
        Calcular(ResultadoVerificacionExterna.Valido, Hoy).Should().Be(EstadoSupervision.Urgente);

    [Fact]
    public void Caducidad_pasada_es_vencido() =>
        Calcular(ResultadoVerificacionExterna.Valido, Hoy.AddDays(-1)).Should().Be(EstadoSupervision.Vencido);
}
