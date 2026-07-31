using CaeManager.Infrastructure.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Contrato de <c>ISanitizadorHtmlService</c> frente al hallazgo N-1 de
/// INFORME-AUDITORIA-2.md. Se prueba contra la implementación real, no contra
/// un doble: lo que hay que garantizar es que el sanitizador configurado como
/// lo está configurado corta estos vectores, no que alguien llame a un método.
/// </summary>
public class GanssSanitizadorHtmlServiceTests
{
    private readonly GanssSanitizadorHtmlService _sanitizador = new();

    [Theory]
    // El vector exacto del informe: un manejador inline dispara aunque el
    // marcado se inserte por DOM, así que "script no corre vía innerHTML" no
    // es defensa.
    [InlineData("<img src=x onerror=\"alert(1)\">")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<div onclick=\"alert(1)\">texto</div>")]
    [InlineData("<svg/onload=alert(1)>")]
    [InlineData("<iframe src=\"https://ajeno.example\"></iframe>")]
    [InlineData("<a href=\"javascript:alert(1)\">pincha</a>")]
    [InlineData("<body onload=alert(1)>")]
    [InlineData("<object data=\"x\"></object>")]
    [InlineData("<style>body{display:none}</style>")]
    public void Elimina_todo_lo_ejecutable(string entrada)
    {
        var saneado = _sanitizador.Sanear(entrada);

        saneado.Should().NotContain("alert");
        saneado.Should().NotContain("onerror");
        saneado.Should().NotContain("onload");
        saneado.Should().NotContain("onclick");
        saneado.Should().NotContain("javascript:");
        saneado.Should().NotContain("<script");
        saneado.Should().NotContain("<iframe");
        saneado.Should().NotContain("<style");
    }

    [Fact]
    public void Conserva_el_formato_legitimo_de_un_correo()
    {
        var saneado = _sanitizador.Sanear(
            "<p>Buenos días,</p><p>Adjunto el <strong>certificado</strong>.</p><ul><li>Uno</li></ul>");

        saneado.Should().Contain("<p>").And.Contain("<strong>").And.Contain("<li>");
        saneado.Should().Contain("Buenos días").And.Contain("certificado");
    }

    [Fact]
    public void Un_enlace_externo_se_abre_aislado_de_la_aplicacion()
    {
        var saneado = _sanitizador.Sanear("<a href=\"https://ejemplo.test\">ver</a>");

        saneado.Should().Contain("href=\"https://ejemplo.test\"");
        saneado.Should().Contain("target=\"_blank\"");
        saneado.Should().Contain("noopener");
    }

    [Fact]
    public void Un_formulario_incrustado_no_puede_hacerse_pasar_por_la_interfaz()
    {
        // Phishing con la marca del propio producto: el correo pinta un
        // formulario de credenciales dentro de una pantalla autenticada.
        var saneado = _sanitizador.Sanear(
            "<form action=\"https://atacante.test\"><input name=\"clave\" type=\"password\"></form>");

        saneado.Should().NotContain("<form").And.NotContain("<input");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Un_cuerpo_vacio_no_revienta(string? entrada)
    {
        _sanitizador.Sanear(entrada).Should().BeEmpty();
    }
}
