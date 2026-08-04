using System.Security.Cryptography;
using System.Text;
using CaeManager.Web.Api.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Verificación de la firma X-Hub-Signature-256 del webhook de WhatsApp:
/// HMAC-SHA256 del cuerpo crudo con el App Secret, en tiempo constante
/// (docs/MULTITENANCY.md § 8 — el secreto se verifica antes que nada).
/// </summary>
public class WebhookWhatsAppFirmaTests
{
    private const string Payload = """{"object":"whatsapp_business_account","entry":[]}""";
    private const string AppSecret = "secreto-de-la-app";

    private static string FirmaCorrecta(string payload, string secreto) =>
        "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secreto), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    [Fact]
    public void Acepta_una_firma_correcta()
    {
        WebhookWhatsAppEndpoints.FirmaValida(Payload, FirmaCorrecta(Payload, AppSecret), AppSecret).Should().BeTrue();
    }

    [Fact]
    public void Acepta_la_firma_en_mayusculas()
    {
        var firma = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(AppSecret), Encoding.UTF8.GetBytes(Payload)));

        WebhookWhatsAppEndpoints.FirmaValida(Payload, firma, AppSecret).Should().BeTrue();
    }

    [Fact]
    public void Rechaza_una_firma_de_otro_secreto()
    {
        WebhookWhatsAppEndpoints.FirmaValida(Payload, FirmaCorrecta(Payload, "otro-secreto"), AppSecret).Should().BeFalse();
    }

    [Fact]
    public void Rechaza_un_payload_manipulado()
    {
        var firma = FirmaCorrecta(Payload, AppSecret);

        WebhookWhatsAppEndpoints.FirmaValida(Payload + " ", firma, AppSecret).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sinprefijo")]
    [InlineData("sha256=no-es-hexadecimal")]
    public void Rechaza_cabeceras_ausentes_o_malformadas(string? cabecera)
    {
        WebhookWhatsAppEndpoints.FirmaValida(Payload, cabecera, AppSecret).Should().BeFalse();
    }

    [Fact]
    public void Rechaza_todo_sin_app_secret_configurado()
    {
        WebhookWhatsAppEndpoints.FirmaValida(Payload, FirmaCorrecta(Payload, AppSecret), null).Should().BeFalse();
    }
}
