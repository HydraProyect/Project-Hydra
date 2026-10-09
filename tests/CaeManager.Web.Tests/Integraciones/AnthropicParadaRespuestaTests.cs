using System.Net;
using System.Text;
using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.AsistenteIa;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>
/// La API de Anthropic responde HTTP 200 también cuando no completa la
/// respuesta: <c>stop_reason: "refusal"</c> si el modelo declina y
/// <c>"max_tokens"</c> si la respuesta se corta. Estas pruebas fijan que las
/// rutas de extracción no dan por buena una respuesta así —una transcripción a
/// medias pasaría por el documento entero— y la forma de la solicitud que sale
/// (modelo, tope y esfuerzo).
/// </summary>
public sealed class AnthropicParadaRespuestaTests
{
    private static readonly IOptions<AnthropicOptions> Opciones =
        Options.Create(new AnthropicOptions { ApiKey = "sk-ant-de-prueba" });

    [Fact]
    public async Task Ocr_cortado_por_el_tope_de_tokens_falla_en_vez_de_devolver_la_transcripcion_a_medias()
    {
        var manejador = new ManejadorFijo(Respuesta("max_tokens", "Primera mitad del documento"));
        var proveedor = new AnthropicDocumentAIProvider(new HttpClient(manejador), Opciones, NullLogger<AnthropicDocumentAIProvider>.Instance);

        var resultado = await proveedor.ExtraerTextoAsync([1, 2, 3], "documento.pdf");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("DocumentAIProvider.RespuestaIncompleta");
    }

    [Fact]
    public async Task Ocr_terminado_devuelve_la_transcripcion()
    {
        var manejador = new ManejadorFijo(Respuesta("end_turn", "Texto completo del documento"));
        var proveedor = new AnthropicDocumentAIProvider(new HttpClient(manejador), Opciones, NullLogger<AnthropicDocumentAIProvider>.Instance);

        var resultado = await proveedor.ExtraerTextoAsync([1, 2, 3], "documento.pdf");

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Texto.Should().Be("Texto completo del documento");
    }

    [Fact]
    public async Task Clasificacion_declinada_por_el_modelo_falla_con_codigo_propio()
    {
        var manejador = new ManejadorFijo("""{"content":[],"stop_reason":"refusal"}""");
        var servicio = new AnthropicDeteccionRelevanciaCaeService(new HttpClient(manejador), Opciones, NullLogger<AnthropicDeteccionRelevanciaCaeService>.Instance);

        var resultado = await servicio.DetectarAsync("Conversación de prueba");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("DeteccionRelevanciaCae.RespuestaIncompleta");
    }

    [Fact]
    public async Task Clasificacion_con_json_cortado_por_el_tope_falla_como_incompleta_y_no_como_invalida()
    {
        var manejador = new ManejadorFijo(Respuesta("max_tokens", """{"esAccionableCae": true, "resumen": "Piden el alta del"""));
        var servicio = new AnthropicDeteccionRelevanciaCaeService(new HttpClient(manejador), Opciones, NullLogger<AnthropicDeteccionRelevanciaCaeService>.Instance);

        var resultado = await servicio.DetectarAsync("Conversación de prueba");

        resultado.Error.Codigo.Should().Be("DeteccionRelevanciaCae.RespuestaIncompleta");
    }

    [Fact]
    public async Task Chat_declinado_por_el_modelo_falla_como_rechazo()
    {
        var manejador = new ManejadorFijo("""{"content":[],"stop_reason":"refusal"}""");
        var servicio = new AnthropicAsistenteIaService(new HttpClient(manejador), Opciones, NullLogger<AnthropicAsistenteIaService>.Instance);

        var resultado = await servicio.PreguntarAsync([new MensajeChatDto(RolMensajeChat.Usuario, "Pregunta")], CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("AsistenteIa.RespuestaRechazada");
    }

    [Fact]
    public async Task Chat_cortado_por_el_tope_entrega_el_texto_que_hay()
    {
        var manejador = new ManejadorFijo(Respuesta("max_tokens", "Respuesta larga que se quedó sin"));
        var servicio = new AnthropicAsistenteIaService(new HttpClient(manejador), Opciones, NullLogger<AnthropicAsistenteIaService>.Instance);

        var resultado = await servicio.PreguntarAsync([new MensajeChatDto(RolMensajeChat.Usuario, "Pregunta")], CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be("Respuesta larga que se quedó sin");
    }

    [Fact]
    public async Task La_solicitud_de_clasificacion_lleva_el_modelo_el_tope_y_el_esfuerzo_configurados()
    {
        var manejador = new ManejadorFijo(Respuesta("end_turn", """{"esAccionableCae": false, "resumen": "Sin gestión", "confianza": 90}"""));
        var servicio = new AnthropicDeteccionRelevanciaCaeService(new HttpClient(manejador), Opciones, NullLogger<AnthropicDeteccionRelevanciaCaeService>.Instance);

        await servicio.DetectarAsync("Conversación de prueba");

        using var solicitud = JsonDocument.Parse(manejador.CuerpoRecibido!);
        solicitud.RootElement.GetProperty("model").GetString().Should().Be("claude-haiku-5-5");
        solicitud.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(16000);
        solicitud.RootElement.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("low");
    }

    [Fact]
    public async Task La_solicitud_del_chat_lleva_su_propio_esfuerzo()
    {
        var manejador = new ManejadorFijo(Respuesta("end_turn", "Respuesta"));
        var servicio = new AnthropicAsistenteIaService(new HttpClient(manejador), Opciones, NullLogger<AnthropicAsistenteIaService>.Instance);

        await servicio.PreguntarAsync([new MensajeChatDto(RolMensajeChat.Usuario, "Pregunta")], CancellationToken.None);

        using var solicitud = JsonDocument.Parse(manejador.CuerpoRecibido!);
        solicitud.RootElement.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("medium");
    }

    private static string Respuesta(string motivoParada, string texto) =>
        JsonSerializer.Serialize(new
        {
            content = new object[]
            {
                // Un bloque de razonamiento delante del texto, como responde la API con el razonamiento activo.
                new { type = "thinking", thinking = "" },
                new { type = "text", text = texto }
            },
            stop_reason = motivoParada,
            model = "claude-haiku-5-5",
            usage = new { input_tokens = 10, output_tokens = 5 }
        });

    private sealed class ManejadorFijo(string cuerpoRespuesta) : HttpMessageHandler
    {
        public string? CuerpoRecibido { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CuerpoRecibido = await request.Content!.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(cuerpoRespuesta, Encoding.UTF8, "application/json")
            };
        }
    }
}
