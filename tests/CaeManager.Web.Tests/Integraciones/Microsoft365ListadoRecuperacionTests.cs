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

    [Fact]
    public async Task La_continuacion_se_pide_tal_cual()
    {
        const string enlace = "https://graph.microsoft.com/v1.0/users/x/messages?$skiptoken=abc";
        var manejador = new Respuesta(HttpStatusCode.OK, "{\"value\":[]}");

        await Cliente(manejador).ListarRecibidosDesdeAsync("token", Compartido, DateTime.UtcNow, enlace, CancellationToken.None);

        manejador.Pedidas.Single().Should().Be(new Uri(enlace));
    }

    [Fact]
    public async Task Una_continuacion_fuera_de_Graph_falla_sin_enviar_el_token()
    {
        var manejador = new Respuesta(HttpStatusCode.OK, "{\"value\":[]}");

        var resultado = await Cliente(manejador).ListarRecibidosDesdeAsync(
            "token", Compartido, DateTime.UtcNow, "https://evil.test/v1.0/robar", CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        manejador.Pedidas.Should().BeEmpty();
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
