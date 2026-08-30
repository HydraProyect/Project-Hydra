using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Comunicaciones;

/// <summary>Auditoría módulo 6: CuerpoHtml no tenía ningún tope defensivo — un cuerpo entrante podía crecer sin cota.</summary>
public class MensajeTests
{
    private static Mensaje CrearMensaje(string cuerpoHtml) =>
        new(Guid.NewGuid(), DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", cuerpoHtml,
            new DateTime(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Un_cuerpo_normal_no_se_altera()
    {
        var mensaje = CrearMensaje("<p>Hola, adjunto el certificado.</p>");

        mensaje.CuerpoHtml.Should().Be("<p>Hola, adjunto el certificado.</p>");
    }

    [Fact]
    public void Un_cuerpo_que_supera_el_maximo_se_trunca_en_vez_de_perder_el_mensaje()
    {
        var cuerpoDemasiadoLargo = new string('a', Mensaje.LongitudMaximaCuerpoHtml + 1000);

        var mensaje = CrearMensaje(cuerpoDemasiadoLargo);

        mensaje.CuerpoHtml.Should().HaveLength(Mensaje.LongitudMaximaCuerpoHtml);
    }

    [Fact]
    public void Un_cuerpo_exactamente_en_el_limite_no_se_trunca()
    {
        var cuerpoEnElLimite = new string('a', Mensaje.LongitudMaximaCuerpoHtml);

        var mensaje = CrearMensaje(cuerpoEnElLimite);

        mensaje.CuerpoHtml.Should().HaveLength(Mensaje.LongitudMaximaCuerpoHtml);
    }
}
