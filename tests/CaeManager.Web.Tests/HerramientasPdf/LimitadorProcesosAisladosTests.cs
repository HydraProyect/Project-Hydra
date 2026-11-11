using CaeManager.Infrastructure.HerramientasPdf;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests.HerramientasPdf;

/// <summary>
/// El estado degradado del limitador por sí mismo: un turno retenido no vuelve nunca y, desde entonces, no se da ningún
/// turno, tampoco a quien ya esperaba hueco cuando se degradó.
/// </summary>
public sealed class LimitadorProcesosAisladosTests
{
    [Fact]
    public async Task Un_turno_retenido_no_se_suelta_al_desecharlo_y_el_limitador_queda_degradado()
    {
        var limitador = new LimitadorProcesosAislados(1);
        var turno = await limitador.EntrarAsync(TimeSpan.Zero, CancellationToken.None);

        turno!.RetenerPorHijoSinConfirmar();
        turno.Dispose();

        limitador.Libres.Should().Be(0);
        limitador.Degradado.Should().BeTrue();
        (await limitador.EntrarAsync(TimeSpan.Zero, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Quien_ya_esperaba_hueco_cuando_se_degrado_no_recibe_turno_y_el_hueco_vuelve()
    {
        // Con capacidad 2: un turno degrada, el otro se suelta y despierta a quien esperaba.
        var limitador = new LimitadorProcesosAislados(2);
        var degradado = await limitador.EntrarAsync(TimeSpan.Zero, CancellationToken.None);
        var otro = await limitador.EntrarAsync(TimeSpan.Zero, CancellationToken.None);
        var esperando = limitador.EntrarAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

        degradado!.RetenerPorHijoSinConfirmar();
        otro!.Dispose();

        (await esperando.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeNull("el limitador se degradó mientras esperaba");
        limitador.Libres.Should().Be(1, "el hueco que recibió y no usa vuelve; el retenido no");
    }

    [Fact]
    public async Task Sin_degradar_un_turno_desechado_se_suelta_una_sola_vez()
    {
        var limitador = new LimitadorProcesosAislados(1);
        var turno = await limitador.EntrarAsync(TimeSpan.Zero, CancellationToken.None);

        turno!.Dispose();
        turno.Dispose();

        limitador.Libres.Should().Be(1);
        limitador.Degradado.Should().BeFalse();
    }
}
