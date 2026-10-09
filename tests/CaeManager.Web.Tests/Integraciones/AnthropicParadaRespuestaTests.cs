using System.Net;
using System.Text;
using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.AsistenteIa;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
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
/// (modelo, tope y esfuerzo, con el ajuste propio de cada ruta).
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
    public async Task Sin_ajuste_propio_la_ruta_envia_el_modelo_el_tope_y_el_esfuerzo_generales()
    {
        var manejador = new ManejadorFijo(Respuesta("end_turn", """{"esAccionableCae": false, "resumen": "Sin gestión", "confianza": 90}"""));
        var servicio = new AnthropicDeteccionRelevanciaCaeService(new HttpClient(manejador), Opciones, NullLogger<AnthropicDeteccionRelevanciaCaeService>.Instance);

        await servicio.DetectarAsync("Conversación de prueba");

        using var solicitud = JsonDocument.Parse(manejador.CuerpoRecibido!);
        solicitud.RootElement.GetProperty("model").GetString().Should().Be("claude-sonnet-5");
        solicitud.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(16000);
        solicitud.RootElement.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("high");
    }

    [Fact]
    public async Task El_chat_envia_el_modelo_y_el_esfuerzo_de_su_ruta()
    {
        var opciones = OpcionesConRutaPropia(RutasAnthropic.Asistente);
        var manejador = new ManejadorFijo(Respuesta("end_turn", "Respuesta"));
        var servicio = new AnthropicAsistenteIaService(new HttpClient(manejador), opciones, NullLogger<AnthropicAsistenteIaService>.Instance);

        await servicio.PreguntarAsync([new MensajeChatDto(RolMensajeChat.Usuario, "Pregunta")], CancellationToken.None);

        using var solicitud = JsonDocument.Parse(manejador.CuerpoRecibido!);
        solicitud.RootElement.GetProperty("model").GetString().Should().Be("modelo-de-la-ruta");
        solicitud.RootElement.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("max");
    }

    /// <summary>
    /// Las cinco rutas de clasificación y extracción, cada una con el código
    /// de error que le corresponde: quitar la comprobación de
    /// <c>stop_reason</c> en cualquiera de ellas pone una fila en rojo.
    /// </summary>
    public static TheoryData<string, string> RutasDeExtraccion => new()
    {
        { RutasAnthropic.RelevanciaCae, "DeteccionRelevanciaCae.RespuestaIncompleta" },
        { RutasAnthropic.VisitaCorreo, "DeteccionVisitaCorreo.RespuestaIncompleta" },
        { RutasAnthropic.GestionCorreo, "DeteccionGestionCorreo.RespuestaIncompleta" },
        { RutasAnthropic.Trabajadores, "ExtraccionTrabajadores.RespuestaIncompleta" },
        { RutasAnthropic.Ocr, "DocumentAIProvider.RespuestaIncompleta" },
        { RutasAnthropic.ExtraccionEstructurada, "DocumentAIProvider.RespuestaIncompleta" },
    };

    [Theory]
    [MemberData(nameof(RutasDeExtraccion))]
    public async Task Cada_ruta_de_extraccion_falla_como_incompleta_ante_un_rechazo(string ruta, string codigoEsperado)
    {
        var manejador = new ManejadorFijo("""{"content":[],"stop_reason":"refusal"}""");

        var error = await LlamarAsync(ruta, manejador);

        error!.Codigo.Should().Be(codigoEsperado);
    }

    [Theory]
    [MemberData(nameof(RutasDeExtraccion))]
    public async Task Cada_ruta_de_extraccion_falla_como_incompleta_ante_un_motivo_de_parada_que_no_es_fin_normal(string ruta, string codigoEsperado)
    {
        var manejador = new ManejadorFijo(Respuesta("model_context_window_exceeded", """{"esAccionableCae": true}"""));

        var error = await LlamarAsync(ruta, manejador);

        error!.Codigo.Should().Be(codigoEsperado);
    }

    [Theory]
    [MemberData(nameof(RutasDeExtraccion))]
    public async Task Cada_ruta_de_extraccion_envia_el_modelo_y_el_esfuerzo_de_su_propia_clave(string ruta, string codigoEsperado)
    {
        _ = codigoEsperado;
        var manejador = new ManejadorFijo(Respuesta("end_turn", "{}"));

        await LlamarAsync(ruta, manejador, OpcionesConRutaPropia(ruta));

        using var solicitud = JsonDocument.Parse(manejador.CuerpoRecibido!);
        solicitud.RootElement.GetProperty("model").GetString().Should().Be("modelo-de-la-ruta");
        solicitud.RootElement.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("max");
    }

    [Theory]
    [MemberData(nameof(RutasDeExtraccion))]
    public async Task El_ajuste_de_otra_ruta_no_alcanza_a_esta(string ruta, string codigoEsperado)
    {
        _ = codigoEsperado;
        var manejador = new ManejadorFijo(Respuesta("end_turn", "{}"));

        await LlamarAsync(ruta, manejador, OpcionesConRutaPropia(RutasAnthropic.Asistente));

        using var solicitud = JsonDocument.Parse(manejador.CuerpoRecibido!);
        solicitud.RootElement.GetProperty("model").GetString().Should().Be("modelo-general");
        solicitud.RootElement.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("low");
    }

    [Fact]
    public void Una_ruta_que_solo_fija_el_modelo_hereda_el_esfuerzo_general()
    {
        var opciones = new AnthropicOptions { Esfuerzo = "low" };
        opciones.Rutas[RutasAnthropic.Ocr] = new RutaAnthropicOptions { Modelo = "modelo-de-la-ruta" };

        opciones.Para(RutasAnthropic.Ocr).Should().Be(("modelo-de-la-ruta", "low"));
    }

    [Fact]
    public void El_ajuste_de_una_ruta_se_lee_de_la_configuracion_sin_distinguir_mayusculas_en_la_clave()
    {
        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Anthropic:Modelo"] = "modelo-general",
                ["Anthropic:Rutas:ocr:Modelo"] = "modelo-de-la-ruta",
                ["Anthropic:Rutas:ocr:Esfuerzo"] = "low",
            })
            .Build();

        var opciones = new AnthropicOptions();
        configuracion.GetSection(AnthropicOptions.SeccionConfiguracion).Bind(opciones);

        opciones.Para(RutasAnthropic.Ocr).Should().Be(("modelo-de-la-ruta", "low"));
        opciones.Para(RutasAnthropic.Trabajadores).Should().Be(("modelo-general", "high"));
    }

    private static IOptions<AnthropicOptions> OpcionesConRutaPropia(string ruta)
    {
        var opciones = new AnthropicOptions { ApiKey = "sk-ant-de-prueba", Modelo = "modelo-general", Esfuerzo = "low" };
        opciones.Rutas[ruta] = new RutaAnthropicOptions { Modelo = "modelo-de-la-ruta", Esfuerzo = "max" };
        return Options.Create(opciones);
    }

    [Theory]
    [InlineData("end_turn")]
    [InlineData("stop_sequence")]
    public async Task Un_fin_normal_no_se_trata_como_incompleto(string motivoParada)
    {
        var manejador = new ManejadorFijo(Respuesta(motivoParada, """{"esAccionableCae": false, "resumen": "Sin gestión", "confianza": 90}"""));

        var error = await LlamarAsync(RutasAnthropic.RelevanciaCae, manejador);

        error.Should().BeNull();
    }

    /// <summary>Llama a la ruta y devuelve su error, o null si terminó con éxito.</summary>
    private static async Task<CaeManager.Domain.Common.Error?> LlamarAsync(
        string ruta, ManejadorFijo manejador, IOptions<AnthropicOptions>? opciones = null)
    {
        opciones ??= Opciones;
        var http = new HttpClient(manejador);

        switch (ruta)
        {
            case RutasAnthropic.RelevanciaCae:
                {
                    var r = await new AnthropicDeteccionRelevanciaCaeService(http, opciones, NullLogger<AnthropicDeteccionRelevanciaCaeService>.Instance)
                        .DetectarAsync("Conversación de prueba");
                    return r.EsFallido ? r.Error : null;
                }
            case RutasAnthropic.VisitaCorreo:
                {
                    var r = await new AnthropicDeteccionVisitaCorreoService(http, opciones, NullLogger<AnthropicDeteccionVisitaCorreoService>.Instance)
                        .DetectarAsync("Correo de prueba", [], new DateOnly(2026, 10, 9));
                    return r.EsFallido ? r.Error : null;
                }
            case RutasAnthropic.GestionCorreo:
                {
                    var r = await new AnthropicDeteccionGestionCorreoService(http, opciones, NullLogger<AnthropicDeteccionGestionCorreoService>.Instance)
                        .DetectarAsync("Correo de prueba", [], []);
                    return r.EsFallido ? r.Error : null;
                }
            case RutasAnthropic.Trabajadores:
                {
                    var r = await new AnthropicExtraccionTrabajadoresIaService(http, opciones, NullLogger<AnthropicExtraccionTrabajadoresIaService>.Instance)
                        .ExtraerAsync([1, 2, 3]);
                    return r.EsFallido ? r.Error : null;
                }
            case RutasAnthropic.Ocr:
                {
                    var r = await new AnthropicDocumentAIProvider(http, opciones, NullLogger<AnthropicDocumentAIProvider>.Instance)
                        .ExtraerTextoAsync([1, 2, 3], "documento.pdf");
                    return r.EsFallido ? r.Error : null;
                }
            case RutasAnthropic.ExtraccionEstructurada:
                {
                    var r = await new AnthropicDocumentAIProvider(http, opciones, NullLogger<AnthropicDocumentAIProvider>.Instance)
                        .ExtraerEstructuradoAsync("Texto del documento", "Formación");
                    return r.EsFallido ? r.Error : null;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(ruta), ruta, "Ruta de extracción desconocida.");
        }
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
