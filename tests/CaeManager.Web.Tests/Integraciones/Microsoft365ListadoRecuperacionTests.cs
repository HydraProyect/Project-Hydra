using System.Net;
using CaeManager.Application.Integraciones;
using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>Issue #2124: el listado de la Bandeja de entrada con el que la recuperación del hueco lee el correo perdido.</summary>
public class Microsoft365ListadoRecuperacionTests
{
    private sealed class Respuesta(HttpStatusCode codigo, string cuerpo) : HttpMessageHandler
    {
        public List<Uri> Pedidas { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Pedidas.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(codigo) { Content = new StringContent(cuerpo) });
        }
    }

    private static Microsoft365GraphClient Cliente(HttpMessageHandler manejador) =>
        new(new HttpClient(manejador), Options.Create(new Microsoft365GraphOptions()), NullLogger<Microsoft365GraphClient>.Instance);

    private static readonly BuzonGraph Compartido = new("cae@operador.test", true);

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"value\":null}")]
    [InlineData("{\"value\":{}}")]
    [InlineData("{\"value\":[null]}")]
    [InlineData("{\"value\":[{\"id\":\"m1\"}]}")]
    [InlineData("{\"value\":[{\"receivedDateTime\":\"2026-11-15T10:00:00Z\"}]}")]
    [InlineData("{\"value\":[{\"id\":\"\",\"receivedDateTime\":\"2026-11-15T10:00:00Z\"}]}")]
    [InlineData("{\"value\":[{\"id\":\"m1\",\"receivedDateTime\":\"no es fecha\"}]}")]
    [InlineData("{\"value\":[],\"@odata.nextLink\":5}")]
    [InlineData("{\"value\":[],\"@odata.nextLink\":\"\"}")]
    [InlineData("no es JSON")]
    [InlineData("")]
    public void Un_cuerpo_ilegible_no_es_una_pagina(string cuerpo) =>
        Microsoft365GraphClient.LeerPaginaRecibidos(cuerpo).Should().BeNull();

    [Fact]
    public void Una_pagina_legible_trae_sus_mensajes_en_UTC_y_la_continuacion()
    {
        var pagina = Microsoft365GraphClient.LeerPaginaRecibidos(
            "{\"value\":[{\"id\":\"m1\",\"receivedDateTime\":\"2026-11-15T10:00:00Z\"},{\"id\":\"m2\",\"receivedDateTime\":\"2026-11-15T11:30:00+01:00\"}]," +
            "\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users/x/messages?$skiptoken=abc\"}");

        pagina.Should().NotBeNull();
        pagina!.Mensajes.Select(m => m.MensajeExternoId).Should().Equal("m1", "m2");
        pagina.Mensajes[1].RecibidoUtc.Should().Be(new DateTime(2026, 11, 15, 10, 30, 0, DateTimeKind.Utc));
        pagina.SiguienteEnlace.Should().Be("https://graph.microsoft.com/v1.0/users/x/messages?$skiptoken=abc");
    }

    [Fact]
    public void Una_pagina_vacia_sin_continuacion_es_el_final()
    {
        var pagina = Microsoft365GraphClient.LeerPaginaRecibidos("{\"value\":[]}");

        pagina!.Mensajes.Should().BeEmpty();
        pagina.SiguienteEnlace.Should().BeNull();
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/users/x/messages?$skiptoken=abc", true)]
    [InlineData("http://graph.microsoft.com/v1.0/users/x/messages", false)]
    [InlineData("https://graph.microsoft.com.evil.test/v1.0/users/x/messages", false)]
    [InlineData("https://evil.test/v1.0/users/x/messages", false)]
    [InlineData("https://user:pass@graph.microsoft.com/v1.0/users/x/messages", false)]
    [InlineData("https://graph.microsoft.com:8443/v1.0/users/x/messages", false)]
    [InlineData("https://graph.microsoft.com/beta/users/x/messages", false)]
    [InlineData("/v1.0/users/x/messages", false)]
    public void Solo_se_sigue_una_continuacion_bajo_la_raiz_de_Graph(string enlace, bool seSigue) =>
        Microsoft365GraphClient.EsEnlaceDeGraph(enlace).Should().Be(seSigue);

    [Fact]
    public async Task La_primera_pagina_filtra_desde_la_fecha_en_orden_ascendente_sin_skip()
    {
        var manejador = new Respuesta(HttpStatusCode.OK, "{\"value\":[]}");

        var resultado = await Cliente(manejador).ListarRecibidosDesdeAsync(
            "token", Compartido, new DateTime(2026, 11, 15, 9, 30, 15, 750, DateTimeKind.Utc), null, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var url = Uri.UnescapeDataString(manejador.Pedidas.Single().ToString());
        url.Should().Contain("mailFolders('Inbox')/messages")
            .And.Contain("$filter=receivedDateTime ge 2026-11-15T09:30:15Z", "redondea hacia abajo al segundo")
            .And.Contain("$orderby=receivedDateTime asc")
            .And.NotContain("$skip=");
    }

    private static readonly DateTime Cursor = new(2026, 11, 15, 9, 30, 15, DateTimeKind.Utc);

    private const string ContinuacionValida =
        "https://graph.microsoft.com/v1.0/users/cae%40operador.test/mailFolders('Inbox')/messages" +
        "?%24select=id%2CreceivedDateTime&%24filter=receivedDateTime+ge+2026-11-15T09%3A00%3A00Z" +
        "&%24orderby=receivedDateTime+asc&%24top=50&%24skip=50";

    [Theory]
    [InlineData(ContinuacionValida, true)]
    [InlineData("https://graph.microsoft.com/v1.0/users('cae@operador.test')/mailFolders/inbox/messages?$filter=receivedDateTime ge 2026-11-15T09:30:15Z&$orderby=receivedDateTime asc&$skip=50", true)]
    [InlineData("https://graph.microsoft.com/v1.0/users/otro%40operador.test/mailFolders('Inbox')/messages?$filter=receivedDateTime ge 2026-11-15T09:00:00Z&$orderby=receivedDateTime asc&$skip=50", false)]
    [InlineData("https://graph.microsoft.com/v1.0/users/cae%40operador.test/mailFolders('Archive')/messages?$filter=receivedDateTime ge 2026-11-15T09:00:00Z&$orderby=receivedDateTime asc&$skip=50", false)]
    [InlineData("https://graph.microsoft.com/v1.0/users/cae%40operador.test/mailFolders('Inbox')/messages?$filter=receivedDateTime ge 2026-11-15T09:30:16Z&$orderby=receivedDateTime asc&$skip=50", false)]
    [InlineData("https://graph.microsoft.com/v1.0/users/cae%40operador.test/mailFolders('Inbox')/messages?$filter=receivedDateTime ge 2026-11-15T09:00:00Z&$orderby=receivedDateTime desc&$skip=50", false)]
    [InlineData("https://graph.microsoft.com/v1.0/users/cae%40operador.test/mailFolders('Inbox')/messages?$orderby=receivedDateTime asc&$skip=50", false)]
    [InlineData("https://graph.microsoft.com/v1.0/users/cae%40operador.test/mailFolders('Inbox')/messages?$filter=receivedDateTime ge 2026-11-15T09:00:00Z and isRead eq false&$orderby=receivedDateTime asc", false)]
    [InlineData("https://graph.microsoft.com/v1.0/users/cae%40operador.test/mailFolders('Inbox')/messages?$filter=receivedDateTime ge 2026-11-15T09:00:00Z&$filter=receivedDateTime ge 2026-11-16T00:00:00Z&$orderby=receivedDateTime asc", false)]
    [InlineData("https://graph.microsoft.com/v1.0/users/cae%40operador.test/messages?$filter=receivedDateTime ge 2026-11-15T09:00:00Z&$orderby=receivedDateTime asc", false)]
    [InlineData("https://evil.test/v1.0/users/cae%40operador.test/mailFolders('Inbox')/messages?$filter=receivedDateTime ge 2026-11-15T09:00:00Z&$orderby=receivedDateTime asc", false)]
    public void Solo_se_sigue_una_continuacion_del_mismo_listado(string enlace, bool seSigue) =>
        Microsoft365GraphClient.EsContinuacionDelListado(enlace, Compartido, Cursor).Should().Be(seSigue);

    [Fact]
    public void Un_buzon_propio_acepta_la_continuacion_reescrita_como_users() =>
        Microsoft365GraphClient.EsContinuacionDelListado(
            "https://graph.microsoft.com/v1.0/users/0c8f3b1e-0000-0000-0000-000000000000/mailFolders('Inbox')/messages?$filter=receivedDateTime ge 2026-11-15T09:00:00Z&$orderby=receivedDateTime asc&$skip=50",
            new BuzonGraph("yo@cliente.test", false), Cursor).Should().BeTrue();

    [Fact]
    public async Task La_continuacion_se_pide_tal_cual()
    {
        var manejador = new Respuesta(HttpStatusCode.OK, "{\"value\":[]}");

        await Cliente(manejador).ListarRecibidosDesdeAsync("token", Compartido, Cursor, ContinuacionValida, CancellationToken.None);

        manejador.Pedidas.Single().Should().Be(new Uri(ContinuacionValida));
    }

    [Theory]
    [InlineData("https://evil.test/v1.0/robar")]
    [InlineData("https://graph.microsoft.com/v1.0/users/otro%40operador.test/mailFolders('Inbox')/messages?$filter=receivedDateTime ge 2026-11-15T09:00:00Z&$orderby=receivedDateTime asc")]
    public async Task Una_continuacion_ajena_al_listado_se_rechaza_sin_enviar_el_token(string enlace)
    {
        var manejador = new Respuesta(HttpStatusCode.OK, "{\"value\":[]}");

        var resultado = await Cliente(manejador).ListarRecibidosDesdeAsync("token", Compartido, Cursor, enlace, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(CodigosErrorMicrosoft365.ContinuacionRechazada);
        manejador.Pedidas.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, CodigosErrorMicrosoft365.ContinuacionRechazada)]
    [InlineData(HttpStatusCode.NotFound, CodigosErrorMicrosoft365.ContinuacionRechazada)]
    [InlineData(HttpStatusCode.Gone, CodigosErrorMicrosoft365.ContinuacionRechazada)]
    [InlineData(HttpStatusCode.Unauthorized, "Integraciones.Microsoft365.ErrorApi")]
    [InlineData(HttpStatusCode.Forbidden, "Integraciones.Microsoft365.ErrorApi")]
    public async Task Solo_un_rechazo_de_la_continuacion_la_marca_como_rechazada(HttpStatusCode codigo, string esperado)
    {
        var resultado = await Cliente(new Respuesta(codigo, ""))
            .ListarRecibidosDesdeAsync("token", Compartido, Cursor, ContinuacionValida, CancellationToken.None);

        resultado.Error.Codigo.Should().Be(esperado);
    }

    [Fact]
    public async Task Un_400_sin_continuacion_no_es_una_continuacion_rechazada()
    {
        var resultado = await Cliente(new Respuesta(HttpStatusCode.BadRequest, ""))
            .ListarRecibidosDesdeAsync("token", Compartido, Cursor, null, CancellationToken.None);

        resultado.Error.Codigo.Should().NotBe(CodigosErrorMicrosoft365.ContinuacionRechazada);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task Un_200_ilegible_es_un_fallo(string cuerpo)
    {
        var resultado = await Cliente(new Respuesta(HttpStatusCode.OK, cuerpo))
            .ListarRecibidosDesdeAsync("token", Compartido, DateTime.UtcNow, null, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
    }

    [Fact]
    public async Task Un_error_de_Graph_es_un_fallo()
    {
        var resultado = await Cliente(new Respuesta(HttpStatusCode.ServiceUnavailable, ""))
            .ListarRecibidosDesdeAsync("token", Compartido, DateTime.UtcNow, null, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
    }

    [Fact]
    public void El_payload_de_recuperacion_lo_lee_la_ingesta()
    {
        var cliente = Cliente(new Respuesta(HttpStatusCode.OK, ""));

        var payload = cliente.ConstruirPayloadRecuperacion(["m1", "m2"]);

        cliente.ExtraerMensajeIdsDeNotificacion(payload).Should().Equal("m1", "m2");
    }
}
