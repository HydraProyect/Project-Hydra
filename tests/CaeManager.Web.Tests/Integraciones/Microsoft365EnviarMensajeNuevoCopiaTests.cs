using System.Net;
using System.Text;
using System.Text.Json;
using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>
/// La copia (CC) de «Pedir» por buzón conectado viaja en <c>ccRecipients</c> del borrador que crea Graph, y solo cuando la hay: sin copia
/// el campo no se envía (no <c>null</c>), que es como se comportaba antes. Sin red: un manejador captura el cuerpo del borrador.
/// </summary>
public class Microsoft365EnviarMensajeNuevoCopiaTests
{
    private sealed class CapturaBorradorHandler : HttpMessageHandler
    {
        public string? CuerpoDelBorrador { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/me/messages", StringComparison.Ordinal))
            {
                CuerpoDelBorrador = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("""{"id":"m1","conversationId":"c1"}""", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    private static async Task<JsonElement> EnviarAsync(IReadOnlyList<string>? copia)
    {
        var handler = new CapturaBorradorHandler();
        var cliente = new Microsoft365GraphClient(
            new HttpClient(handler), Options.Create(new Microsoft365GraphOptions()), NullLogger<Microsoft365GraphClient>.Instance);

        var resultado = await cliente.EnviarNuevoMensajeAsync(
            "token", "buzon@tenant.example", ["marta@refrielectric.example"], "Asunto", "<p>Hola</p>", null, CancellationToken.None, copia);

        resultado.EsExitoso.Should().BeTrue();
        return JsonDocument.Parse(handler.CuerpoDelBorrador!).RootElement.Clone();
    }

    [Fact]
    public async Task Con_copia_el_borrador_lleva_ccRecipients_y_los_destinatarios_siguen_en_toRecipients()
    {
        var json = await EnviarAsync(["coordinacion@arcosspa.example"]);

        json.GetProperty("toRecipients").EnumerateArray().Select(r => r.GetProperty("emailAddress").GetProperty("address").GetString())
            .Should().Equal("marta@refrielectric.example");
        json.GetProperty("ccRecipients").EnumerateArray().Select(r => r.GetProperty("emailAddress").GetProperty("address").GetString())
            .Should().Equal("coordinacion@arcosspa.example");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sin_copia_el_borrador_no_lleva_ccRecipients(bool listaVacia)
    {
        var json = await EnviarAsync(listaVacia ? [] : null);

        json.TryGetProperty("ccRecipients", out _).Should().BeFalse();
    }
}
