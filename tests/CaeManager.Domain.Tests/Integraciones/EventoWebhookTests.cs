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

        evento.Estado.Should().Be(EstadoEventoWebhook.Pendiente);
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
    public void MarcarEnProceso_lo_marca_procesando_y_limpia_el_siguiente_intento()
    {
        var evento = new EventoWebhook(Guid.NewGuid(), "{}");
        evento.RegistrarFallo("fallo temporal");

        evento.MarcarEnProceso();

        evento.Estado.Should().Be(EstadoEventoWebhook.Procesando);
        evento.SiguienteIntentoEnUtc.Should().BeNull();
    }

    [Fact]
    public void MarcarProcesado_lo_marca_correcto_y_limpia_el_error()
    {
        var evento = new EventoWebhook(Guid.NewGuid(), "{}");
        evento.RegistrarFallo("fallo temporal");

        evento.MarcarProcesado();

        evento.Estado.Should().Be(EstadoEventoWebhook.Completado);
        evento.ErrorProcesado.Should().BeNull();
    }

    [Fact]
    public void RegistrarFallo_incrementa_intentos_sin_agotar_el_maximo_y_fija_backoff()
    {
        var evento = new EventoWebhook(Guid.NewGuid(), "{}");
        var antesDeFallar = DateTime.UtcNow;

        evento.RegistrarFallo("timeout");

        evento.Intentos.Should().Be(1);
        evento.ErrorProcesado.Should().Be("timeout");
        evento.Estado.Should().Be(EstadoEventoWebhook.Pendiente);
        evento.SiguienteIntentoEnUtc.Should().NotBeNull().And.BeAfter(antesDeFallar);
    }

    [Fact]
    public void RegistrarFallo_se_da_por_perdido_tras_el_maximo_de_intentos_sin_dejar_backoff()
    {
        var evento = new EventoWebhook(Guid.NewGuid(), "{}");

        for (var i = 0; i < EventoWebhook.MaximoIntentos; i++)
            evento.RegistrarFallo("fallo persistente");

        evento.Estado.Should().Be(EstadoEventoWebhook.DescartadoDefinitivo);
        evento.Intentos.Should().Be(EventoWebhook.MaximoIntentos);
        evento.SiguienteIntentoEnUtc.Should().BeNull();
    }

    [Fact]
    public void RecuperarSiEstancado_no_hace_nada_si_no_esta_procesando()
    {
        var evento = new EventoWebhook(Guid.NewGuid(), "{}");

        evento.RecuperarSiEstancado(TimeSpan.Zero, DateTime.UtcNow);

        evento.Estado.Should().Be(EstadoEventoWebhook.Pendiente);
    }

    [Fact]
    public void RecuperarSiEstancado_devuelve_a_pendiente_un_procesando_por_encima_del_umbral()
    {
        var evento = new EventoWebhook(Guid.NewGuid(), "{}");
        evento.MarcarEnProceso();

        evento.RecuperarSiEstancado(TimeSpan.Zero, DateTime.UtcNow.AddMilliseconds(1));

        evento.Estado.Should().Be(EstadoEventoWebhook.Pendiente);
        evento.Intentos.Should().Be(1, "recuperarse cuenta como un intento fallido más");
    }

    /// <summary>Auditoría módulo 6: el payload crudo de un webhook contiene PHI/PII de conversación — se redacta pasada la retención, pero nunca mientras el evento aún podría necesitar reintentarse.</summary>
    public class RedactarPayloadTests
    {
        [Fact]
        public void Redacta_un_evento_completado()
        {
            var evento = new EventoWebhook(Guid.NewGuid(), "{\"mensaje\":\"contenido sensible\"}");
            evento.MarcarProcesado();

            evento.RedactarPayload();

            evento.PayloadCrudo.Should().Be(EventoWebhook.MarcadorPayloadRedactado);
            evento.PayloadCrudo.Should().NotContain("contenido sensible");
            evento.PayloadRedactado.Should().BeTrue();
        }

        [Fact]
        public void Redacta_un_evento_descartado_definitivamente()
        {
            var evento = new EventoWebhook(Guid.NewGuid(), "{}");
            for (var i = 0; i < EventoWebhook.MaximoIntentos; i++)
                evento.RegistrarFallo("fallo persistente");

            evento.RedactarPayload();

            evento.PayloadRedactado.Should().BeTrue();
        }

        [Theory]
        [InlineData(false)] // Pendiente
        [InlineData(true)]  // Procesando
        public void Rechaza_redactar_un_evento_que_todavia_puede_reintentarse(bool marcarEnProceso)
        {
            var evento = new EventoWebhook(Guid.NewGuid(), "{}");
            if (marcarEnProceso) evento.MarcarEnProceso();

            var accion = () => evento.RedactarPayload();

            accion.Should().Throw<InvalidOperationException>();
            evento.PayloadRedactado.Should().BeFalse();
        }

        [Fact]
        public void Redactar_dos_veces_no_falla_ni_cambia_nada()
        {
            var evento = new EventoWebhook(Guid.NewGuid(), "{}");
            evento.MarcarProcesado();
            evento.RedactarPayload();

            var accion = () => evento.RedactarPayload();

            accion.Should().NotThrow();
            evento.PayloadCrudo.Should().Be(EventoWebhook.MarcadorPayloadRedactado);
        }
    }
}
