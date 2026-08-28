using CaeManager.Domain.Blindaje42;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Blindaje42;

public class CalculadoraEstadoBlindaje42Tests
{
    private static readonly DateOnly FechaSolicitud = new(2026, 8, 1);

    [Fact]
    public void Sin_respuesta_y_dentro_del_plazo_esta_pendiente() =>
        CalculadoraEstadoBlindaje42.Calcular(null, FechaSolicitud, FechaSolicitud.AddDays(29))
            .Should().Be(EstadoBlindaje42.PendienteRespuesta);

    [Fact]
    public void Sin_respuesta_justo_en_el_limite_sigue_pendiente() =>
        CalculadoraEstadoBlindaje42.Calcular(null, FechaSolicitud, FechaSolicitud.AddDays(SolicitudCertificacionTgss.PlazoDiasTgss))
            .Should().Be(EstadoBlindaje42.PendienteRespuesta);

    [Fact]
    public void Sin_respuesta_pasado_el_plazo_es_exonerada_por_silencio() =>
        CalculadoraEstadoBlindaje42.Calcular(null, FechaSolicitud, FechaSolicitud.AddDays(SolicitudCertificacionTgss.PlazoDiasTgss + 1))
            .Should().Be(EstadoBlindaje42.ExoneradaPorSilencio);

    [Fact]
    public void Sin_descubiertos_es_exonerada_por_certificacion_aunque_ya_haya_pasado_el_plazo() =>
        CalculadoraEstadoBlindaje42.Calcular(
                ResultadoCertificacionTgss.SinDescubiertos, FechaSolicitud, FechaSolicitud.AddDays(90))
            .Should().Be(EstadoBlindaje42.ExoneradaPorCertificacion);

    [Fact]
    public void Con_descubiertos_no_exonera_aunque_llegue_dentro_de_plazo() =>
        CalculadoraEstadoBlindaje42.Calcular(
                ResultadoCertificacionTgss.ConDescubiertos, FechaSolicitud, FechaSolicitud.AddDays(5))
            .Should().Be(EstadoBlindaje42.NoExonerada);
}
