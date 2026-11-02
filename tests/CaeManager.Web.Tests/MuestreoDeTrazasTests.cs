using System.Diagnostics;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Trace;

namespace CaeManager.Web.Tests;

/// <summary>
/// MJ-06: el muestreo de trazas es configurable (<c>Observabilidad:MuestreoTrazas</c>, fracción de 0 a 1). Sin valor se
/// conserva el comportamiento anterior (todo); un valor inválido tumba el arranque en vez de caer en silencio.
/// </summary>
public class MuestreoDeTrazasTests
{
    private static IConfiguration Config(string? valor) => new ConfigurationBuilder()
        .AddInMemoryCollection(valor is null ? [] : new Dictionary<string, string?> { [MuestreoDeTrazas.Clave] = valor })
        .Build();

    [Theory]
    [InlineData(null, 1.0)]
    [InlineData("", 1.0)]
    [InlineData("0", 0.0)]
    [InlineData("0.25", 0.25)]
    [InlineData("1", 1.0)]
    public void Lee_la_fraccion_con_cultura_invariante_y_por_defecto_conserva_todo(string? valor, double esperado) =>
        MuestreoDeTrazas.Leer(Config(valor)).Should().Be(esperado);

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.5")]
    [InlineData("NaN")]
    [InlineData("mucho")]
    [InlineData("0,5")]
    public void Un_valor_fuera_de_rango_o_ilegible_falla_al_arrancar(string valor)
    {
        var leer = () => MuestreoDeTrazas.Leer(Config(valor));

        leer.Should().Throw<InvalidOperationException>().WithMessage($"*{MuestreoDeTrazas.Clave}*");
    }

    private static SamplingResult Muestrear(double fraccion) =>
        MuestreoDeTrazas.Crear(fraccion).ShouldSample(
            new SamplingParameters(default, ActivityTraceId.CreateRandom(), "MiRequest", ActivityKind.Internal));

    [Fact]
    public void Con_fraccion_cero_se_descarta_toda_traza_raiz() =>
        Muestrear(0).Decision.Should().Be(SamplingDecision.Drop);

    [Fact]
    public void Con_fraccion_uno_se_conserva_toda_traza_raiz() =>
        Muestrear(1).Decision.Should().Be(SamplingDecision.RecordAndSample);

    [Fact]
    public void Una_traza_hija_local_sigue_la_decision_de_su_padre()
    {
        var padre = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, isRemote: false);

        var decision = MuestreoDeTrazas.Crear(0).ShouldSample(
            new SamplingParameters(padre, padre.TraceId, "Hija", ActivityKind.Internal));

        decision.Decision.Should().Be(SamplingDecision.RecordAndSample, "no se parte una traza ya iniciada");
    }

    [Fact]
    public void Una_cabecera_traceparent_de_un_cliente_no_fuerza_la_grabacion()
    {
        var padreRemoto = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, isRemote: true);

        var decision = MuestreoDeTrazas.Crear(0).ShouldSample(
            new SamplingParameters(padreRemoto, padreRemoto.TraceId, "GET /", ActivityKind.Server));

        decision.Decision.Should().Be(SamplingDecision.Drop, "con una fracción de 0 un cliente no puede reabrir el volumen de spans con el flag 01");
    }

    [Fact]
    public void Una_cabecera_traceparent_sin_muestrear_no_apaga_la_traza_con_fraccion_uno()
    {
        var padreRemoto = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.None, isRemote: true);

        var decision = MuestreoDeTrazas.Crear(1).ShouldSample(
            new SamplingParameters(padreRemoto, padreRemoto.TraceId, "GET /", ActivityKind.Server));

        decision.Decision.Should().Be(SamplingDecision.RecordAndSample);
    }

    [Fact]
    public void Una_fraccion_intermedia_conserva_aproximadamente_esa_proporcion_de_trazas_raiz()
    {
        var sampler = MuestreoDeTrazas.Crear(0.25);
        const int total = 4000;

        var conservadas = Enumerable.Range(0, total).Count(_ =>
            sampler.ShouldSample(new SamplingParameters(default, ActivityTraceId.CreateRandom(), "R", ActivityKind.Internal)).Decision
                == SamplingDecision.RecordAndSample);

        conservadas.Should().BeInRange((int)(total * 0.20), (int)(total * 0.30));
    }
}
