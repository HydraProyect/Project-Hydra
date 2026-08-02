using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Integraciones;

public class EventoWebhookTests
{
    [Fact]
    public void Se_crea_pendiente_de_procesar()
    {
        var evento = new EventoWebhook(Guid.NewGuid(), "{\"value\":[]}");

        evento.Procesado.Should().BeFalse();
        evento.Intentos.Should().Be(0);
        evento.PayloadCrudo.Should().Be("{\"value\":[]}");
    }

    [Fact]
    public void Rechaza_un_payload_vacio()
    {
        var accion = () => new EventoWebhook(Guid.NewGuid(), " ");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarcarProcesado_lo_marca_correcto_y_limpia_el_error()
    {
        var evento = new EventoWebhook(Guid.NewGuid(), "{}");
        evento.RegistrarFallo("fallo temporal");

        evento.MarcarProcesado();

        evento.Procesado.Should().BeTrue();
        evento.ErrorProcesado.Should().BeNull();
    }

    [Fact]
    public void RegistrarFallo_incrementa_intentos_sin_agotar_el_maximo()
    {
        var evento = new EventoWebhook(Guid.NewGuid(), "{}");

        evento.RegistrarFallo("timeout");

        evento.Intentos.Should().Be(1);
        evento.ErrorProcesado.Should().Be("timeout");
        evento.Procesado.Should().BeFalse();
    }

    [Fact]
    public void RegistrarFallo_se_da_por_perdido_tras_el_maximo_de_intentos()
    {
        var evento = new EventoWebhook(Guid.NewGuid(), "{}");

        for (var i = 0; i < EventoWebhook.MaximoIntentos; i++)
            evento.RegistrarFallo("fallo persistente");

        evento.Procesado.Should().BeTrue();
        evento.Intentos.Should().Be(EventoWebhook.MaximoIntentos);
    }
}
