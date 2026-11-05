using System.Security.Cryptography;
using System.Text;
using CaeManager.Application.Comercial.Common;
using CaeManager.Infrastructure.Comercial;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.Comercial;

/// <summary>
/// StripePaymentProvider vive en Infrastructure, no en Web — se prueba desde
/// aquí porque este proyecto (a diferencia de Application.Tests) referencia
/// CaeManager.Web y, transitivamente, CaeManager.Infrastructure; no hay un
/// proyecto CaeManager.Infrastructure.Tests dedicado en este repo, y esto
/// sigue el mismo precedente que TenantActualTests/BotonAsistenteIaTests
/// (Web.Tests probando tipos públicos de Infrastructure directamente).
///
/// La firma Stripe-Signature se construye a mano siguiendo el algoritmo
/// documentado de Stripe (no un helper del SDK: Stripe.net no expone uno
/// para firmar, solo para verificar) — timestamp Unix + HMAC-SHA256 de
/// "{timestamp}.{payload}" con el webhook secret, formateada como
/// "t={timestamp},v1={hmac}".
/// </summary>
public class StripePaymentProviderTests
{
    private const string WebhookSecret = "whsec_test_secreto";
    private const string ApiKey = "sk_test_falsa";

    private static string FirmarPayload(string payload, string secreto, long? timestamp = null)
    {
        var t = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payloadFirmado = $"{t}.{payload}";
        var hmac = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secreto), Encoding.UTF8.GetBytes(payloadFirmado))).ToLowerInvariant();
        return $"t={t},v1={hmac}";
    }

    private static string EventoSuscripcion(string tipoEvento, string idSuscripcion, string idCliente, string status) =>
        $$"""
        {
          "id": "evt_test_1",
          "object": "event",
          "type": "{{tipoEvento}}",
          "data": {
            "object": {
              "id": "{{idSuscripcion}}",
              "object": "subscription",
              "customer": "{{idCliente}}",
              "status": "{{status}}"
            }
          }
        }
        """;

    private static StripePaymentProvider CrearProveedor(string? apiKey = ApiKey, string? webhookSecret = WebhookSecret) =>
        new(Options.Create(new StripeOptions { ApiKey = apiKey, WebhookSecret = webhookSecret }), NullLogger<StripePaymentProvider>.Instance);

    [Fact]
    public void VerificarYLeerWebhook_acepta_un_evento_con_firma_valida_y_lo_interpreta()
    {
        var payload = EventoSuscripcion("customer.subscription.updated", "sub_123", "cus_456", "past_due");
        var firma = FirmarPayload(payload, WebhookSecret);

        var resultado = CrearProveedor().VerificarYLeerWebhook(payload, firma);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.TipoEvento.Should().Be("customer.subscription.updated");
        resultado.Valor.Suscripcion.Should().NotBeNull();
        resultado.Valor.Suscripcion!.IdSuscripcion.Should().Be("sub_123");
        resultado.Valor.Suscripcion.IdCliente.Should().Be("cus_456");
        resultado.Valor.Suscripcion.Estado.Should().Be(EstadoSuscripcionProveedor.EnPeriodoGracia);
    }

    [Theory]
    [InlineData("active", EstadoSuscripcionProveedor.Activa)]
    [InlineData("trialing", EstadoSuscripcionProveedor.Activa)]
    [InlineData("past_due", EstadoSuscripcionProveedor.EnPeriodoGracia)]
    [InlineData("unpaid", EstadoSuscripcionProveedor.Impagada)]
    [InlineData("canceled", EstadoSuscripcionProveedor.Cancelada)]
    [InlineData("incomplete_expired", EstadoSuscripcionProveedor.Cancelada)]
    [InlineData("incomplete", EstadoSuscripcionProveedor.Desconocida)]
    [InlineData("paused", EstadoSuscripcionProveedor.Desconocida)]
    public void VerificarYLeerWebhook_traduce_cada_status_de_Stripe_al_estado_del_proveedor(
        string statusStripe, EstadoSuscripcionProveedor esperado)
    {
        var payload = EventoSuscripcion("customer.subscription.updated", "sub_123", "cus_456", statusStripe);
        var firma = FirmarPayload(payload, WebhookSecret);

        var resultado = CrearProveedor().VerificarYLeerWebhook(payload, firma);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Suscripcion!.Estado.Should().Be(esperado);
    }

    [Fact]
    public void VerificarYLeerWebhook_reconoce_un_evento_sin_subscription_sin_fallar()
    {
        // p. ej. un evento de tipo customer.* o invoice.* — no todo lo que
        // llega tiene una Subscription en data.object.
        const string payload = """
            {
              "id": "evt_test_2",
              "object": "event",
              "type": "customer.updated",
              "data": { "object": { "id": "cus_456", "object": "customer" } }
            }
            """;
        var firma = FirmarPayload(payload, WebhookSecret);

        var resultado = CrearProveedor().VerificarYLeerWebhook(payload, firma);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Suscripcion.Should().BeNull();
    }

    [Fact]
    public void VerificarYLeerWebhook_rechaza_una_firma_de_otro_secreto()
    {
        var payload = EventoSuscripcion("customer.subscription.updated", "sub_123", "cus_456", "active");
        var firma = FirmarPayload(payload, "whsec_otro_secreto");

        var resultado = CrearProveedor().VerificarYLeerWebhook(payload, firma);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("PaymentProvider.FirmaInvalida");
    }

    [Fact]
    public void VerificarYLeerWebhook_rechaza_un_payload_manipulado_tras_firmarlo()
    {
        var payload = EventoSuscripcion("customer.subscription.updated", "sub_123", "cus_456", "active");
        var firma = FirmarPayload(payload, WebhookSecret);
        var payloadManipulado = payload.Replace("sub_123", "sub_999");

        var resultado = CrearProveedor().VerificarYLeerWebhook(payloadManipulado, firma);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("PaymentProvider.FirmaInvalida");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sin-el-formato-esperado")]
    public void VerificarYLeerWebhook_rechaza_cabeceras_ausentes_o_malformadas(string? firma)
    {
        var payload = EventoSuscripcion("customer.subscription.updated", "sub_123", "cus_456", "active");

        var resultado = CrearProveedor().VerificarYLeerWebhook(payload, firma);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("PaymentProvider.FirmaInvalida");
    }

    [Fact]
    public void VerificarYLeerWebhook_rechaza_todo_sin_webhook_secret_configurado()
    {
        var payload = EventoSuscripcion("customer.subscription.updated", "sub_123", "cus_456", "active");
        var firma = FirmarPayload(payload, WebhookSecret);

        var resultado = CrearProveedor(webhookSecret: null).VerificarYLeerWebhook(payload, firma);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("PaymentProvider.NoConfigurado");
    }

    [Fact]
    public async Task ObtenerSuscripcionAsync_falla_de_forma_controlada_sin_api_key_configurada()
    {
        var resultado = await CrearProveedor(apiKey: null).ObtenerSuscripcionAsync("sub_123");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("PaymentProvider.NoConfigurado");
    }

    /// <summary>
    /// Un fallo de red no sale como excepción (el webhook respondería 500), sino como fallo
    /// controlado del proveedor, que el webhook traduce a 503 para que Stripe reintente.
    /// </summary>
    [Fact]
    public async Task ObtenerSuscripcionAsync_convierte_un_fallo_de_red_en_proveedor_no_disponible()
    {
        var proveedor = new StripePaymentProvider(
            Options.Create(new StripeOptions { ApiKey = ApiKey, WebhookSecret = WebhookSecret }), NullLogger<StripePaymentProvider>.Instance)
        {
            ManejadorHttp = new RedCaida(),
        };

        var resultado = await proveedor.ObtenerSuscripcionAsync("sub_123");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("PaymentProvider.NoDisponible");
    }

    private sealed class RedCaida : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("conexión rechazada");
    }
}
