using CaeManager.Application.Integraciones;
using FluentAssertions;

namespace CaeManager.Application.Tests.Integraciones;

public class DeteccionCorreoAutomaticoTests
{
    [Theory]
    [InlineData("Auto-Submitted", "auto-replied", true)]
    [InlineData("auto-submitted", "auto-generated", true)]
    [InlineData("Auto-Submitted", "no; owner-email=a@b.test", false)]
    [InlineData("X-Autoreply", "yes", true)]
    [InlineData("X-Autoreply", "no", false)]
    [InlineData("Precedence", "bulk", true)]
    [InlineData("Precedence", "list", true)]
    [InlineData("Precedence", "normal", false)]
    public void Detecta_cabeceras_sin_distinguir_mayusculas(string nombre, string valor, bool esperado) =>
        DeteccionCorreoAutomatico.EsAutomatico(new("id", "hilo", "Asunto", "persona@ejemplo.test", "Texto",
            DateTime.UtcNow, [], [], [new(nombre, valor)])).Should().Be(esperado);

    [Theory]
    [InlineData("no-reply@ejemplo.test", true)]
    [InlineData("NOREPLY@ejemplo.test", true)]
    [InlineData("no_reply@ejemplo.test", true)]
    [InlineData("do-not-reply@ejemplo.test", true)]
    [InlineData("persona@noreply.test", false)]
    public void Detecta_el_buzon_no_reply_sin_confundir_el_dominio(string remitente, bool esperado) =>
        DeteccionCorreoAutomatico.EsAutomatico(new("id", "hilo", "Asunto", remitente, "Texto",
            DateTime.UtcNow, [], [])).Should().Be(esperado);

    [Theory]
    [InlineData("List-Id", "<cae.ejemplo.test>", true)]
    [InlineData("list-id", "<cae.ejemplo.test>", true)]
    [InlineData("List-Unsubscribe", "<mailto:baja@ejemplo.test>", true)]
    [InlineData("X-Autorespond", "1", true)]
    [InlineData("Return-Path", "<>", true)]
    [InlineData("Return-Path", " <> ", true)]
    [InlineData("Return-Path", "<persona@ejemplo.test>", false)]
    public void Detecta_listas_de_correo_y_rebotes(string nombre, string valor, bool esperado) =>
        DeteccionCorreoAutomatico.EsAutomatico(new("id", "hilo", "Asunto", "persona@ejemplo.test", "Texto",
            DateTime.UtcNow, [], [], [new(nombre, valor)])).Should().Be(esperado);

    [Theory]
    [InlineData("mailer-daemon@ejemplo.test", true)]
    [InlineData("MAILER_DAEMON@ejemplo.test", true)]
    [InlineData("postmaster@ejemplo.test", true)]
    [InlineData("persona.postmaster@ejemplo.test", false)]
    public void Detecta_el_remitente_de_los_rebotes(string remitente, bool esperado) =>
        DeteccionCorreoAutomatico.EsAutomatico(new("id", "hilo", "Asunto", remitente, "Texto",
            DateTime.UtcNow, [], [])).Should().Be(esperado);

    [Theory]
    [InlineData("Automatic reply: Documentación")]
    [InlineData("automatic reply: documentación")]
    [InlineData("Respuesta automática: Documentación")]
    [InlineData("Out of office")]
    [InlineData("Fuera de la oficina: vuelvo el lunes")]
    [InlineData("Undeliverable: Documentación")]
    [InlineData("No se puede entregar: Documentación")]
    public void Un_asunto_de_autorrespuesta_no_admite_acuse_aunque_no_se_clasifique_como_automatico(string asunto)
    {
        var mensaje = new MensajeGraphDto(
            "id", "hilo", asunto, "persona@ejemplo.test", "Texto", DateTime.UtcNow, [], []);

        DeteccionCorreoAutomatico.EsAutomatico(mensaje).Should().BeFalse();
        DeteccionCorreoAutomatico.NoAdmiteAcuse(mensaje).Should().BeTrue();
    }

    [Theory]
    [InlineData("X-Auto-Response-Suppress", "All")]
    [InlineData("Authentication-Results", "mx.ejemplo.test; dmarc=fail header.from=ejemplo.test")]
    public void Una_cabecera_que_pide_silencio_o_falla_la_autenticacion_no_admite_acuse(string nombre, string valor)
    {
        var mensaje = new MensajeGraphDto(
            "id", "hilo", "Asunto", "persona@ejemplo.test", "Texto", DateTime.UtcNow, [], [], [new(nombre, valor)]);

        DeteccionCorreoAutomatico.EsAutomatico(mensaje).Should().BeFalse();
        DeteccionCorreoAutomatico.NoAdmiteAcuse(mensaje).Should().BeTrue();
    }

    [Fact]
    public void Un_correo_de_una_persona_admite_acuse() =>
        DeteccionCorreoAutomatico.NoAdmiteAcuse(new("id", "hilo", "Documentación de julio", "persona@ejemplo.test", "Texto",
            DateTime.UtcNow, [], [], [new("Authentication-Results", "mx.ejemplo.test; dmarc=pass")])).Should().BeFalse();
}
