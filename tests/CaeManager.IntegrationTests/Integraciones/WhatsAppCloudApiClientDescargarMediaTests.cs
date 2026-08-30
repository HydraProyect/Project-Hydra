using System.Net;
using System.Text.Json;
using CaeManager.Application.Integraciones;
using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// Auditoría módulo 6: el salto 2 de <c>DescargarMediaAsync</c> sigue una URL
/// que sale de un campo JSON de la respuesta de Meta, no de una constante —
/// sin validar host, un metadato inesperado convertiría el cliente en un
/// SSRF que además manda el Bearer token de la línea a quien sea. También
/// prueba que el tamaño real se vuelve a acotar mientras se copia, no solo
/// con el <c>file_size</c> declarado en los metadatos.
/// </summary>
public class WhatsAppCloudApiClientDescargarMediaTests
{
    private sealed class RespuestasEncoladasHandler(params HttpResponseMessage[] respuestas) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _respuestas = new(respuestas);
        public List<string> UrlsPeticionadas { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UrlsPeticionadas.Add(request.RequestUri!.ToString());
            return Task.FromResult(_respuestas.Dequeue());
        }
    }

    private static WhatsAppCloudApiClient CrearCliente(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Options.Create(new WhatsAppCloudApiOptions()), NullLogger<WhatsAppCloudApiClient>.Instance);

    private static HttpResponseMessage RespuestaMetadatos(string url, string mimeType = "image/jpeg", long? fileSize = null)
    {
        var json = fileSize is null
            ? JsonSerializer.Serialize(new { url, mime_type = mimeType })
            : JsonSerializer.Serialize(new { url, mime_type = mimeType, file_size = fileSize.Value });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    }

    [Fact]
    public async Task Rechaza_una_url_de_media_con_host_ajeno_a_meta_y_nunca_la_descarga()
    {
        var handler = new RespuestasEncoladasHandler(RespuestaMetadatos("https://atacante.example/robo"));
        var cliente = CrearCliente(handler);

        var resultado = await cliente.DescargarMediaAsync("token-de-la-linea", "media-1", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        // Solo la petición de metadatos — nunca se llegó a pedir "atacante.example".
        handler.UrlsPeticionadas.Should().ContainSingle();
    }

    [Fact]
    public async Task Rechaza_una_url_de_media_por_http_sin_cifrar()
    {
        var handler = new RespuestasEncoladasHandler(RespuestaMetadatos("http://lookaside.fbsbx.com/sin-tls"));
        var cliente = CrearCliente(handler);

        var resultado = await cliente.DescargarMediaAsync("token-de-la-linea", "media-1", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        handler.UrlsPeticionadas.Should().ContainSingle();
    }

    [Fact]
    public async Task Acepta_una_url_de_media_de_un_host_de_meta()
    {
        var handler = new RespuestasEncoladasHandler(
            RespuestaMetadatos("https://lookaside.fbsbx.com/whatsapp_business/attachments/foo"),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        var cliente = CrearCliente(handler);

        var resultado = await cliente.DescargarMediaAsync("token-de-la-linea", "media-1", CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Contenido.Should().Equal(1, 2, 3);
        handler.UrlsPeticionadas.Should().HaveCount(2);
    }

    /// <summary>El file_size de los metadatos es un dato declarado por Meta, no una garantía — el límite real se aplica mientras se copian los bytes.</summary>
    [Fact]
    public async Task Corta_la_descarga_si_el_contenido_real_supera_el_limite_aunque_los_metadatos_no_lo_avisaran()
    {
        var contenidoDemasiadoGrande = new byte[LimitesMediaWhatsApp.TamanoMaximoBytes + 1];
        var handler = new RespuestasEncoladasHandler(
            RespuestaMetadatos("https://lookaside.fbsbx.com/x"), // sin file_size: el pre-chequeo no lo detecta
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(contenidoDemasiadoGrande) });
        var cliente = CrearCliente(handler);

        var resultado = await cliente.DescargarMediaAsync("token-de-la-linea", "media-1", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.WhatsApp.MediaDemasiadoGrande");
    }

    [Fact]
    public async Task Rechaza_por_file_size_declarado_sin_llegar_a_pedir_el_binario()
    {
        var handler = new RespuestasEncoladasHandler(
            RespuestaMetadatos("https://lookaside.fbsbx.com/x", fileSize: LimitesMediaWhatsApp.TamanoMaximoBytes + 1));
        var cliente = CrearCliente(handler);

        var resultado = await cliente.DescargarMediaAsync("token-de-la-linea", "media-1", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.WhatsApp.MediaDemasiadoGrande");
        handler.UrlsPeticionadas.Should().ContainSingle();
    }
}
