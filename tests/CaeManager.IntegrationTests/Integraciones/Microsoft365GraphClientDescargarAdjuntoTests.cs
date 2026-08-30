using System.Net;
using CaeManager.Application.Integraciones;
using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// Auditoría módulo 6: <c>ObtenerContenidoAdjuntoAsync</c> pasó de
/// <c>$select=contentBytes</c> (todo el JSON en memoria, contenido en base64
/// con ~33% de amplificación) a <c>$value</c> con copia acotada — el tamaño
/// que declaran los metadatos del mensaje es un pre-filtro, no una garantía
/// sobre los bytes reales.
/// </summary>
public class Microsoft365GraphClientDescargarAdjuntoTests
{
    private sealed class RespuestaUnicaHandler(HttpResponseMessage respuesta) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respuesta);
    }

    private static Microsoft365GraphClient CrearCliente(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Options.Create(new Microsoft365GraphOptions()), NullLogger<Microsoft365GraphClient>.Instance);

    [Fact]
    public async Task Descarga_el_contenido_binario_de_un_adjunto()
    {
        var handler = new RespuestaUnicaHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([10, 20, 30]) });
        var cliente = CrearCliente(handler);

        var resultado = await cliente.ObtenerContenidoAdjuntoAsync("token", "mensaje-1", "adjunto-1", CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Equal(10, 20, 30);
    }

    [Fact]
    public async Task Corta_la_descarga_si_el_contenido_real_supera_el_limite()
    {
        var contenidoDemasiadoGrande = new byte[LimitesAdjuntosCorreo.TamanoMaximoDescargaBytes + 1];
        var handler = new RespuestaUnicaHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(contenidoDemasiadoGrande) });
        var cliente = CrearCliente(handler);

        var resultado = await cliente.ObtenerContenidoAdjuntoAsync("token", "mensaje-1", "adjunto-1", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.AdjuntoDemasiadoGrande");
    }

    [Fact]
    public async Task Propaga_un_fallo_si_graph_responde_con_error()
    {
        var handler = new RespuestaUnicaHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        var cliente = CrearCliente(handler);

        var resultado = await cliente.ObtenerContenidoAdjuntoAsync("token", "mensaje-1", "adjunto-1", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Microsoft365.ErrorApi");
    }
}
