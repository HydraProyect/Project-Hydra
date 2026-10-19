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
}
