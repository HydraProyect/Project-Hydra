using System.Net;
using System.Text;
using System.Text.Json;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Integraciones;
using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>
/// La marca de contenido de Vega revisado por una persona (issue #2007, D-5) viaja en <c>internetMessageHeaders</c> del
/// mensaje que crea Graph, en /reply y en el borrador de un mensaje nuevo, y solo cuando se pide: sin ella la propiedad
/// no se envía y la petición queda como antes. El cuerpo no cambia. Sin red: un manejador captura las peticiones.
/// </summary>
public class Microsoft365CabeceraOrigenContenidoTests
{
    private const string Cuerpo = "<p>Ya hemos recibido su documentación.</p>";

    private sealed class CapturaHandler : HttpMessageHandler
    {
        public string? CuerpoRespuesta { get; private set; }
        public string? CuerpoBorrador { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ruta = request.RequestUri!.AbsolutePath;
            if (ruta.EndsWith("/reply", StringComparison.Ordinal))
            {
                CuerpoRespuesta = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            if (ruta.EndsWith("/me/messages", StringComparison.Ordinal))
            {
                CuerpoBorrador = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("""{"id":"m1","conversationId":"c1"}""", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    private static Microsoft365GraphClient Cliente(CapturaHandler handler) =>
        new(new HttpClient(handler), Options.Create(new Microsoft365GraphOptions()), NullLogger<Microsoft365GraphClient>.Instance);

    private static async Task<JsonElement> ResponderAsync(bool contenidoIaRevisada, IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos = null)
    {
        var handler = new CapturaHandler();
        var resultado = await Cliente(handler).EnviarRespuestaAsync(
            "token", new BuzonGraph("buzon@tenant.example", false), "origen-1", Cuerpo, adjuntos, CancellationToken.None, contenidoIaRevisada);

        resultado.EsExitoso.Should().BeTrue();
        return JsonDocument.Parse(handler.CuerpoRespuesta!).RootElement.Clone();
    }

    private static async Task<JsonElement> EnviarNuevoAsync(bool contenidoIaRevisada)
    {
        var handler = new CapturaHandler();
        var resultado = await Cliente(handler).EnviarNuevoMensajeAsync(
            "token", new BuzonGraph("buzon@tenant.example", false), ["contrata@empresa.example"], "Asunto", Cuerpo, null,
            _ => Task.FromResult(true), CancellationToken.None, contenidoIaRevisada: contenidoIaRevisada);

        resultado.EsExitoso.Should().BeTrue();
        return JsonDocument.Parse(handler.CuerpoBorrador!).RootElement.Clone();
    }

    private static IEnumerable<(string? Nombre, string? Valor)> Cabeceras(JsonElement mensaje) =>
        mensaje.GetProperty("internetMessageHeaders").EnumerateArray()
            .Select(c => (c.GetProperty("name").GetString(), c.GetProperty("value").GetString()));

    [Fact]
    public async Task La_respuesta_de_Vega_revisada_lleva_la_cabecera_y_el_mismo_cuerpo()
    {
        var json = await ResponderAsync(contenidoIaRevisada: true);

        Cabeceras(json.GetProperty("message")).Should().Equal(("X-TALVEG-Origen-Contenido", "ia-revisada"));
        json.GetProperty("message").TryGetProperty("attachments", out _).Should().BeFalse();
        json.GetProperty("comment").GetString().Should().Be(Cuerpo);
    }

    [Fact]
    public async Task La_respuesta_sin_marca_no_lleva_message_como_antes()
    {
        var json = await ResponderAsync(contenidoIaRevisada: false);

        json.TryGetProperty("message", out var mensaje).Should().BeTrue();
        mensaje.ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("comment").GetString().Should().Be(Cuerpo);
    }

    [Fact]
    public async Task La_respuesta_con_adjuntos_y_marca_lleva_las_dos_cosas()
    {
        var json = await ResponderAsync(true, [new AdjuntoParaEnviarDto("a.pdf", "application/pdf", [1, 2, 3])]);

        var mensaje = json.GetProperty("message");
        mensaje.GetProperty("attachments").GetArrayLength().Should().Be(1);
        Cabeceras(mensaje).Should().ContainSingle();
    }

    [Fact]
    public async Task La_respuesta_con_adjuntos_sin_marca_no_lleva_cabeceras()
    {
        var json = await ResponderAsync(false, [new AdjuntoParaEnviarDto("a.pdf", "application/pdf", [1, 2, 3])]);

        json.GetProperty("message").TryGetProperty("internetMessageHeaders", out _).Should().BeFalse();
    }

    [Fact]
    public async Task El_borrador_de_Vega_revisado_lleva_la_cabecera()
    {
        var json = await EnviarNuevoAsync(contenidoIaRevisada: true);

        Cabeceras(json).Should().Equal((OrigenContenidoEnvio.NombreCabecera, OrigenContenidoEnvio.ValorCabeceraIaRevisada));
        json.GetProperty("body").GetProperty("content").GetString().Should().Be(Cuerpo);
    }

    [Fact]
    public async Task El_borrador_sin_marca_no_lleva_cabeceras()
    {
        var json = await EnviarNuevoAsync(contenidoIaRevisada: false);

        json.TryGetProperty("internetMessageHeaders", out _).Should().BeFalse();
    }
}
