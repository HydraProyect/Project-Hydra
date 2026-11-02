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
    [InlineData("X-Autorespond", "1", true)]
    [InlineData("Return-Path", "<>", true)]
    [InlineData("Return-Path", " <> ", true)]
    [InlineData("Return-Path", "<persona@ejemplo.test>", false)]
    public void Detecta_autorrespuestas_y_rebotes(string nombre, string valor, bool esperado) =>
        DeteccionCorreoAutomatico.EsAutomatico(new("id", "hilo", "Asunto", "persona@ejemplo.test", "Texto",
            DateTime.UtcNow, [], [], [new(nombre, valor)])).Should().Be(esperado);

    [Theory]
    [InlineData("List-Id", "<cae.ejemplo.test>")]
    [InlineData("list-id", "<cae.ejemplo.test>")]
    [InlineData("List-Unsubscribe", "<mailto:baja@ejemplo.test>")]
    [InlineData("List-Unsubscribe-Post", "List-Unsubscribe=One-Click")]
    [InlineData("List-Subscribe", "<mailto:alta@ejemplo.test>")]
    [InlineData("List-Post", "<mailto:lista@ejemplo.test>")]
    [InlineData("List-Post", "NO")]
    [InlineData("List-Help", "<mailto:ayuda@ejemplo.test>")]
    [InlineData("List-Owner", "<mailto:dueno@ejemplo.test>")]
    [InlineData("List-Archive", "<https://ejemplo.test/archivo>")]
    [InlineData("Mailing-List", "contact lista@ejemplo.test")]
    [InlineData("X-Mailing-List", "lista@ejemplo.test")]
    [InlineData("X-Mailman-Version", "3.3.9")]
    [InlineData("X-BeenThere", "lista@ejemplo.test")]
    public void Una_cabecera_de_lista_excluye_el_acuse_sin_clasificar_el_correo_como_automatico(string nombre, string valor)
    {
        var mensaje = new MensajeGraphDto(
            "id", "hilo", "Asunto", "persona@ejemplo.test", "Texto", DateTime.UtcNow, [], [], [new(nombre, valor)]);

        DeteccionCorreoAutomatico.NoAdmiteAcuse(mensaje).Should().BeTrue();
        DeteccionCorreoAutomatico.EsAutomatico(mensaje).Should().BeFalse("una persona también escribe a través de una lista");
    }

    [Fact]
    public void Una_cabecera_de_lista_vacia_no_excluye_el_acuse() =>
        DeteccionCorreoAutomatico.NoAdmiteAcuse(new("id", "hilo", "Asunto", "persona@ejemplo.test", "Texto",
            DateTime.UtcNow, [], [], [new("List-Id", " ")])).Should().BeFalse();

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
    [InlineData("Resposta automàtica: Documentació")]
    [InlineData("Accepted: Reunión de coordinación")]
    [InlineData("Rechazado: Reunión de coordinación")]
    [InlineData("Read: Documentación")]
    [InlineData("Leído: Documentación")]
    public void Un_asunto_de_autorrespuesta_no_admite_acuse_aunque_no_se_clasifique_como_automatico(string asunto)
    {
        var mensaje = new MensajeGraphDto(
            "id", "hilo", asunto, "persona@ejemplo.test", "Texto", DateTime.UtcNow, [], []);

        DeteccionCorreoAutomatico.EsAutomatico(mensaje).Should().BeFalse();
        DeteccionCorreoAutomatico.NoAdmiteAcuse(mensaje).Should().BeTrue();
    }

    [Theory]
    [InlineData("X-Auto-Response-Suppress", "All")]
    [InlineData("In-Reply-To", "<anterior@ejemplo.test>")]
    [InlineData("References", "<anterior@ejemplo.test>")]
    public void Una_cabecera_que_pide_silencio_o_responde_a_un_correo_anterior_no_admite_acuse(string nombre, string valor)
    {
        var mensaje = new MensajeGraphDto(
            "id", "hilo", "Asunto", "persona@ejemplo.test", "Texto", DateTime.UtcNow, [], [], [new(nombre, valor)]);

        DeteccionCorreoAutomatico.EsAutomatico(mensaje).Should().BeFalse();
        DeteccionCorreoAutomatico.NoAdmiteAcuse(mensaje).Should().BeTrue();
    }

    [Theory]
    [InlineData("noresponder@ejemplo.test")]
    [InlineData("no-responda@ejemplo.test")]
    [InlineData("no.contestar@ejemplo.test")]
    [InlineData("bounces@ejemplo.test")]
    [InlineData("notifications@ejemplo.test")]
    public void Un_remitente_que_no_lee_respuestas_no_admite_acuse_sin_clasificarse_como_automatico(string remitente)
    {
        var mensaje = new MensajeGraphDto("id", "hilo", "Asunto", remitente, "Texto", DateTime.UtcNow, [], []);

        DeteccionCorreoAutomatico.EsAutomatico(mensaje).Should().BeFalse();
        DeteccionCorreoAutomatico.NoAdmiteAcuse(mensaje).Should().BeTrue();
    }

    [Fact]
    public void Un_correo_de_una_persona_admite_acuse() =>
        DeteccionCorreoAutomatico.NoAdmiteAcuse(new("id", "hilo", "Documentación de julio", "persona@ejemplo.test", "Texto",
            DateTime.UtcNow, [], [], [new("Authentication-Results", "mx.ejemplo.test; dmarc=pass")])).Should().BeFalse();
}
