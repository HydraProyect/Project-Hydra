using System.Text.Json;
using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests.Integraciones;

public class Microsoft365NotificacionesLoteTests
{
    [Fact]
    public void Un_lote_con_el_mismo_secreto_y_suscripcion_admite_todos_los_mensajes()
    {
        using var http = new HttpClient();
        var cliente = CrearCliente(http);
        var payload = CrearLote("secreto-de-prueba", "suscripcion-de-prueba");

        cliente.ExtraerClientStateDeNotificacion(payload).Should().Be("secreto-de-prueba");
        cliente.ExtraerSubscriptionIdDeNotificacion(payload).Should().Be("suscripcion-de-prueba");
        cliente.ExtraerMensajeIdsDeNotificacion(payload).Should().Equal("mensaje-1", "mensaje-2");
    }

    [Theory]
    [InlineData("otro-secreto", "suscripcion-de-prueba")]
    [InlineData(null, "suscripcion-de-prueba")]
    [InlineData("", "suscripcion-de-prueba")]
    [InlineData(" ", "suscripcion-de-prueba")]
    [InlineData("secreto-de-prueba", "otra-suscripcion")]
    [InlineData("secreto-de-prueba", null)]
    [InlineData("secreto-de-prueba", "")]
    [InlineData("secreto-de-prueba", " ")]
    public void Una_notificacion_posterior_sin_credenciales_coincidentes_rechaza_el_lote(
        string? secreto, string? suscripcion)
    {
        using var http = new HttpClient();
        var cliente = CrearCliente(http);
        var payload = CrearLote(secreto, suscripcion);

        cliente.ExtraerClientStateDeNotificacion(payload).Should().BeNull();
        cliente.ExtraerSubscriptionIdDeNotificacion(payload).Should().BeNull();
    }

    [Theory]
    [InlineData("{\"value\":[]}")]
    [InlineData("{\"value\":[null]}")]
    [InlineData("{\"value\":[{}]}")]
    [InlineData("{\"value\":[{\"clientState\":\"secreto\",\"subscriptionId\":\"suscripcion\"},null]}")]
    [InlineData("{}")]
    [InlineData("no es JSON")]
    public void Un_lote_sin_notificaciones_validas_no_aporta_credenciales(string payload)
    {
        using var http = new HttpClient();
        var cliente = CrearCliente(http);

        cliente.ExtraerClientStateDeNotificacion(payload).Should().BeNull();
        cliente.ExtraerSubscriptionIdDeNotificacion(payload).Should().BeNull();
    }

    private static Microsoft365GraphClient CrearCliente(HttpClient http) =>
        new(http, Options.Create(new Microsoft365GraphOptions()), NullLogger<Microsoft365GraphClient>.Instance);

    private static string CrearLote(string? secreto, string? suscripcion) => JsonSerializer.Serialize(new
    {
        value = new[]
        {
            new { clientState = (string?)"secreto-de-prueba", subscriptionId = (string?)"suscripcion-de-prueba", resourceData = new { id = "mensaje-1" } },
            new { clientState = secreto, subscriptionId = suscripcion, resourceData = new { id = "mensaje-2" } }
        }
    });
}
