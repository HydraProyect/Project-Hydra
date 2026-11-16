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
/// mensaje que crea Graph. Una respuesta con marca no usa /reply: crea el borrador (createReply), comprueba que lleva la
/// cabecera y solo entonces lo envía; si no la lleva, lo borra y falla. Sin marca, la petición queda como antes. El cuerpo
/// no cambia. Sin red: un manejador captura las peticiones y simula a Graph.
/// </summary>
public class Microsoft365CabeceraOrigenContenidoTests
{
    private const string Cuerpo = "<p>Ya hemos recibido su documentación.</p>";

    private sealed class GraphSimulado : HttpMessageHandler
    {
        /// <summary>Lo que Graph devuelve al leer las cabeceras del borrador; null, que no las guardó.</summary>
        public string? CabecerasDelBorrador { get; set; } =
            $$"""{"internetMessageHeaders":[{"name":"{{OrigenContenidoEnvio.NombreCabecera}}","value":"{{OrigenContenidoEnvio.ValorCabeceraIaRevisada}}"}]}""";

        /// <summary>Lo que Graph responde a createReply.</summary>
        public HttpStatusCode EstadoCrearRespuesta { get; set; } = HttpStatusCode.Created;

        /// <summary>La lectura de las cabeceras del borrador falla con esta excepción (red o timeout de HttpClient).</summary>
        public Exception? FalloDeLectura { get; set; }

        /// <summary>Se cancela al leer las cabeceras del borrador, como una petición que el usuario abandona a medias.</summary>
        public CancellationTokenSource? CancelarAlLeer { get; set; }

        /// <summary>Si el borrado del borrador llegó con su token ya cancelado (no se habría ejecutado).</summary>
        public bool? BorradoConTokenCancelado { get; private set; }

        public List<string> Llamadas { get; } = [];
        public string? CuerpoRespuesta { get; private set; }
        public string? CuerpoCrearRespuesta { get; private set; }
        public string? CuerpoBorrador { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ruta = request.RequestUri!.AbsolutePath;
            Llamadas.Add($"{request.Method} {ruta.Split('/').Last()}");

            if (request.Method == HttpMethod.Post && ruta.EndsWith("/reply", StringComparison.Ordinal))
            {
                CuerpoRespuesta = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            if (request.Method == HttpMethod.Post && ruta.EndsWith("/createReply", StringComparison.Ordinal))
            {
                CuerpoCrearRespuesta = await request.Content!.ReadAsStringAsync(cancellationToken);
                return EstadoCrearRespuesta == HttpStatusCode.Created
                    ? Json(HttpStatusCode.Created, """{"id":"borrador-1","conversationId":"c1"}""")
                    : Json(EstadoCrearRespuesta, """{"error":{"code":"ErrorAccessDenied"}}""");
            }

            if (request.Method == HttpMethod.Post && ruta.EndsWith("/me/messages", StringComparison.Ordinal))
            {
                CuerpoBorrador = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json(HttpStatusCode.Created, """{"id":"m1","conversationId":"c1"}""");
            }

            if (request.Method == HttpMethod.Get)
            {
                if (FalloDeLectura is not null) throw FalloDeLectura;
                if (CancelarAlLeer is not null)
                {
                    await CancelarAlLeer.CancelAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return CabecerasDelBorrador is null
                    ? Json(HttpStatusCode.OK, """{"internetMessageHeaders":null}""")
                    : Json(HttpStatusCode.OK, CabecerasDelBorrador);
            }

            if (request.Method == HttpMethod.Delete)
                BorradoConTokenCancelado = cancellationToken.IsCancellationRequested;

            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }

        private static HttpResponseMessage Json(HttpStatusCode estado, string json) =>
            new(estado) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static Microsoft365GraphClient Cliente(GraphSimulado graph) =>
        new(new HttpClient(graph), Options.Create(new Microsoft365GraphOptions()), NullLogger<Microsoft365GraphClient>.Instance);

    private static Task<CaeManager.Domain.Common.Result> ResponderAsync(
        GraphSimulado graph, bool contenidoIaRevisada, IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos = null, bool compartido = false) =>
        Cliente(graph).EnviarRespuestaAsync(
            "token", new BuzonGraph("buzon@tenant.example", compartido), "origen-1", Cuerpo, adjuntos, CancellationToken.None,
            contenidoIaRevisada: contenidoIaRevisada);

    /// <summary>
    /// Un buzón compartido sin permiso de escritura responde por /reply pero no puede crear el borrador que exige la marca:
    /// el error lo dice, para que no parezca un fallo de Microsoft.
    /// </summary>
    [Fact]
    public async Task Un_compartido_sin_permiso_de_escritura_lo_nombra_al_responder_con_marca()
    {
        var graph = new GraphSimulado { EstadoCrearRespuesta = HttpStatusCode.Forbidden };

        var resultado = await ResponderAsync(graph, contenidoIaRevisada: true, compartido: true);

        resultado.Error.Codigo.Should().Be(CodigosErrorMicrosoft365.SinPermisoEscritura);
        graph.Llamadas.Should().Equal("POST createReply");
    }

    public static TheoryData<string> FallosDeLectura => ["red", "timeout"];

    /// <summary>Un fallo de red o un timeout al leer las cabeceras no deja el borrador huérfano: se borra y el envío falla.</summary>
    [Theory]
    [MemberData(nameof(FallosDeLectura))]
    public async Task Si_la_lectura_de_las_cabeceras_falla_el_borrador_se_borra(string fallo)
    {
        var graph = new GraphSimulado
        {
            FalloDeLectura = fallo == "red" ? new HttpRequestException("red caída") : new TaskCanceledException("timeout de HttpClient"),
        };

        var resultado = await ResponderAsync(graph, contenidoIaRevisada: true);

        resultado.EsFallido.Should().BeTrue();
        graph.Llamadas.Should().Equal("POST createReply", "GET borrador-1", "DELETE borrador-1");
    }

    /// <summary>
    /// Si se cancela la petición mientras se leen las cabeceras, el borrador ya creado se borra con un token propio (no con
    /// el cancelado) y la cancelación sigue subiendo.
    /// </summary>
    [Fact]
    public async Task Si_se_cancela_al_leer_las_cabeceras_el_borrador_se_borra_y_la_cancelacion_sube()
    {
        using var cts = new CancellationTokenSource();
        var graph = new GraphSimulado { CancelarAlLeer = cts };

        var envio = () => Cliente(graph).EnviarRespuestaAsync(
            "token", new BuzonGraph("buzon@tenant.example", false), "origen-1", Cuerpo, null, cts.Token,
            contenidoIaRevisada: true);

        await envio.Should().ThrowAsync<OperationCanceledException>();
        graph.Llamadas.Should().Equal("POST createReply", "GET borrador-1", "DELETE borrador-1");
        graph.BorradoConTokenCancelado.Should().BeFalse();
    }

    private static async Task<JsonElement> EnviarNuevoAsync(bool contenidoIaRevisada)
    {
        var graph = new GraphSimulado();
        var resultado = await Cliente(graph).EnviarNuevoMensajeAsync(
            "token", new BuzonGraph("buzon@tenant.example", false), ["contrata@empresa.example"], "Asunto", Cuerpo, null,
            _ => Task.FromResult(true), CancellationToken.None, contenidoIaRevisada: contenidoIaRevisada);

        resultado.EsExitoso.Should().BeTrue();
        return JsonDocument.Parse(graph.CuerpoBorrador!).RootElement.Clone();
    }

    private static IEnumerable<(string? Nombre, string? Valor)> Cabeceras(JsonElement mensaje) =>
        mensaje.GetProperty("internetMessageHeaders").EnumerateArray()
            .Select(c => (c.GetProperty("name").GetString(), c.GetProperty("value").GetString()));

    [Fact]
    public async Task La_respuesta_de_Vega_revisada_crea_el_borrador_con_la_cabecera_lo_comprueba_y_lo_envia()
    {
        var graph = new GraphSimulado();

        var resultado = await ResponderAsync(graph, contenidoIaRevisada: true);

        resultado.EsExitoso.Should().BeTrue();
        graph.Llamadas.Should().Equal("POST createReply", "GET borrador-1", "POST send");
        var json = JsonDocument.Parse(graph.CuerpoCrearRespuesta!).RootElement;
        Cabeceras(json.GetProperty("message")).Should().Equal(("X-TALVEG-Origen-Contenido", "ia-revisada"));
        json.GetProperty("message").TryGetProperty("attachments", out _).Should().BeFalse();
        json.GetProperty("comment").GetString().Should().Be(Cuerpo);
    }

    /// <summary>Si Graph no guardó la cabecera, el correo no sale sin marca: se borra el borrador y el envío falla.</summary>
    [Fact]
    public async Task Si_el_borrador_no_lleva_la_cabecera_no_se_envia_y_se_borra()
    {
        var graph = new GraphSimulado { CabecerasDelBorrador = null };

        var resultado = await ResponderAsync(graph, contenidoIaRevisada: true);

        resultado.EsFallido.Should().BeTrue();
        graph.Llamadas.Should().Equal("POST createReply", "GET borrador-1", "DELETE borrador-1");
    }

    [Fact]
    public async Task Si_el_borrador_lleva_otro_valor_tampoco_se_envia()
    {
        var graph = new GraphSimulado
        {
            CabecerasDelBorrador = $$"""{"internetMessageHeaders":[{"name":"{{OrigenContenidoEnvio.NombreCabecera}}","value":"otra"}]}""",
        };

        var resultado = await ResponderAsync(graph, contenidoIaRevisada: true);

        resultado.EsFallido.Should().BeTrue();
        graph.Llamadas.Should().NotContain("POST send");
    }

    [Fact]
    public async Task La_respuesta_sin_marca_va_por_reply_sin_message_como_antes()
    {
        var graph = new GraphSimulado();

        var resultado = await ResponderAsync(graph, contenidoIaRevisada: false);

        resultado.EsExitoso.Should().BeTrue();
        graph.Llamadas.Should().Equal("POST reply");
        var json = JsonDocument.Parse(graph.CuerpoRespuesta!).RootElement;
        json.GetProperty("message").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("comment").GetString().Should().Be(Cuerpo);
    }

    [Fact]
    public async Task La_respuesta_con_adjuntos_y_marca_lleva_las_dos_cosas()
    {
        var graph = new GraphSimulado();

        await ResponderAsync(graph, true, [new AdjuntoParaEnviarDto("a.pdf", "application/pdf", [1, 2, 3])]);

        var mensaje = JsonDocument.Parse(graph.CuerpoCrearRespuesta!).RootElement.GetProperty("message");
        mensaje.GetProperty("attachments").GetArrayLength().Should().Be(1);
        Cabeceras(mensaje).Should().ContainSingle();
    }

    [Fact]
    public async Task La_respuesta_con_adjuntos_sin_marca_no_lleva_cabeceras()
    {
        var graph = new GraphSimulado();

        await ResponderAsync(graph, false, [new AdjuntoParaEnviarDto("a.pdf", "application/pdf", [1, 2, 3])]);

        JsonDocument.Parse(graph.CuerpoRespuesta!).RootElement.GetProperty("message")
            .TryGetProperty("internetMessageHeaders", out _).Should().BeFalse();
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
