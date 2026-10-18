using System.Net;
using System.Text;
using System.Text.Json;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Infrastructure.AsistenteIa;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests.Integraciones;

public class AnthropicDeteccionIntencionCaeTests
{
    [Theory]
    [InlineData("{\"intencion\":99,\"confianza\":3}", "end_turn")]
    [InlineData("{\"intencion\":1,\"confianza\":100}", "end_turn")]
    [InlineData("{\"intencion\":1,\"confianza\":3}", "max_tokens")]
    [InlineData("Esto no es JSON", "end_turn")]
    public async Task Una_propuesta_invalida_o_incompleta_no_se_da_por_buena(string texto, string motivoParada)
    {
        using var cliente = new HttpClient(new RespuestaFija(JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = texto } },
            stop_reason = motivoParada
        })));
        var resultado = await Crear(cliente).DetectarAsync("Coordinación de acceso");
        resultado.EsFallido.Should().BeTrue();
    }

    [Fact]
    public async Task El_catalogo_y_la_confianza_validos_se_interpretan_como_propuesta()
    {
        using var cliente = new HttpClient(new RespuestaFija("""{"content":[{"type":"text","text":"{\"intencion\":5,\"confianza\":3}"}],"stop_reason":"end_turn"}"""));
        var resultado = await Crear(cliente).DetectarAsync("Acceso bloqueado");
        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Intencion.Should().Be(IntencionCae.UrgenciaAcceso);
        resultado.Valor.Confianza.Should().Be(ConfianzaIntencion.Alta);
    }

    [Fact]
    public async Task Sin_clave_no_se_contacta_con_el_proveedor()
    {
        using var cliente = new HttpClient(new TiempoAgotado());
        var servicio = new AnthropicDeteccionIntencionCaeService(cliente, Options.Create(new AnthropicOptions()),
            NullLogger<AnthropicDeteccionIntencionCaeService>.Instance);
        var resultado = await servicio.DetectarAsync("Un mensaje");
        resultado.Error.Codigo.Should().Be("DeteccionIntencionCae.NoConfigurado");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_timeout_o_la_respuesta_malformada_permiten_el_respaldo_por_reglas(bool timeout)
    {
        using var cliente = new HttpClient(timeout ? new TiempoAgotado() : new RespuestaFija("no es JSON"));
        var resultado = await Crear(cliente).DetectarAsync("Un mensaje");
        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(timeout ? "DeteccionIntencionCae.TiempoAgotado" : "DeteccionIntencionCae.RespuestaInvalida");
    }

    private static AnthropicDeteccionIntencionCaeService Crear(HttpClient cliente) =>
        new(cliente, Options.Create(new AnthropicOptions { ApiKey = "clave-de-prueba-sin-red" }),
            NullLogger<AnthropicDeteccionIntencionCaeService>.Instance);

    private sealed class RespuestaFija(string contenido) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(contenido, Encoding.UTF8, "application/json") });
    }

    private sealed class TiempoAgotado : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException();
    }
}
