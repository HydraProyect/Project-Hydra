using CaeManager.Application.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Common;

public class VentanaSaludOperativaTests
{
    // Mismo tamaño de ventana que la clase real (privado, no se expone
    // aparte para no duplicar un "número mágico" que ya vive documentado
    // en VentanaSaludOperativa).
    private const int TamanoVentana = 50;

    [Fact]
    public void No_emite_nada_mientras_la_ventana_no_se_completa()
    {
        var alerta = new AlertaOperativaFalsa();
        var ventana = new VentanaSaludOperativa(alerta);

        // Todos fallidos, pero muy por debajo del tamaño de ventana: sin
        // suficientes muestras, ni siquiera se evalúa el umbral.
        for (var i = 0; i < TamanoVentana - 1; i++)
            ventana.RegistrarDesenlace(fallo: true, lento: false);

        alerta.Alertas.Should().BeEmpty();
    }

    [Fact]
    public void Emite_alerta_critica_de_tasa_de_error_al_completar_la_ventana_por_encima_del_umbral()
    {
        var alerta = new AlertaOperativaFalsa();
        var ventana = new VentanaSaludOperativa(alerta);

        // 30% de fallos > 20% de umbral.
        for (var i = 0; i < TamanoVentana; i++)
            ventana.RegistrarDesenlace(fallo: i < 15, lento: false);

        alerta.Alertas.Should().ContainSingle();
        var (mensaje, nivel) = alerta.Alertas.Single();
        nivel.Should().Be(NivelAlertaOperativa.Critica);
        mensaje.Should().Contain("Tasa de error");
    }

    [Fact]
    public void No_emite_alerta_de_errores_si_la_tasa_queda_por_debajo_del_umbral()
    {
        var alerta = new AlertaOperativaFalsa();
        var ventana = new VentanaSaludOperativa(alerta);

        // 10% de fallos, por debajo del 20% de umbral.
        for (var i = 0; i < TamanoVentana; i++)
            ventana.RegistrarDesenlace(fallo: i < 5, lento: false);

        alerta.Alertas.Should().BeEmpty();
    }

    [Fact]
    public void Emite_aviso_de_latencia_degradada_al_completar_la_ventana_por_encima_del_umbral()
    {
        var alerta = new AlertaOperativaFalsa();
        var ventana = new VentanaSaludOperativa(alerta);

        // 40% de requests lentos > 30% de umbral, sin ningún fallo — las dos
        // alertas son independientes entre sí.
        for (var i = 0; i < TamanoVentana; i++)
            ventana.RegistrarDesenlace(fallo: false, lento: i < 20);

        alerta.Alertas.Should().ContainSingle();
        var (mensaje, nivel) = alerta.Alertas.Single();
        nivel.Should().Be(NivelAlertaOperativa.Aviso);
        mensaje.Should().Contain("Latencia degradada");
    }

    [Fact]
    public void Puede_emitir_las_dos_alertas_a_la_vez_si_ambos_umbrales_se_superan()
    {
        var alerta = new AlertaOperativaFalsa();
        var ventana = new VentanaSaludOperativa(alerta);

        for (var i = 0; i < TamanoVentana; i++)
            ventana.RegistrarDesenlace(fallo: i < 15, lento: i < 20);

        alerta.Alertas.Should().HaveCount(2);
        alerta.Alertas.Should().Contain(a => a.Nivel == NivelAlertaOperativa.Critica);
        alerta.Alertas.Should().Contain(a => a.Nivel == NivelAlertaOperativa.Aviso);
    }

    [Fact]
    public void Reinicia_los_contadores_tras_evaluar_una_ventana()
    {
        var alerta = new AlertaOperativaFalsa();
        var ventana = new VentanaSaludOperativa(alerta);

        // Primera ventana: 100% de fallos, dispara una alerta.
        for (var i = 0; i < TamanoVentana; i++)
            ventana.RegistrarDesenlace(fallo: true, lento: false);
        alerta.Alertas.Should().ContainSingle();

        // Segunda ventana: sin fallos. Si los contadores no se hubieran
        // reiniciado, seguiría arrastrando el 100% de la ventana anterior.
        for (var i = 0; i < TamanoVentana; i++)
            ventana.RegistrarDesenlace(fallo: false, lento: false);

        alerta.Alertas.Should().ContainSingle("la segunda ventana no debería haber añadido una alerta nueva");
    }
}
