using System.Net;
using CaeManager.Application.Integraciones;
using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>
/// Issue #2124: el listado de la carpeta Inbox con el que la recuperación del hueco lee el correo perdido. Cada página es
/// la primera de una consulta por fecha; la continuación de Graph solo dice si hay más y nunca se pide.
/// </summary>
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
    [InlineData("{\"value\":[{\"id\":\"m2\",\"receivedDateTime\":\"2026-11-15T10:00:01Z\"},{\"id\":\"m1\",\"receivedDateTime\":\"2026-11-15T10:00:00Z\"}]}")]
    [InlineData("no es JSON")]
    [InlineData("")]
    public void Un_cuerpo_ilegible_no_es_una_pagina(string cuerpo) =>
        Microsoft365GraphClient.LeerPaginaRecibidos(cuerpo).Should().BeNull();

    [Fact]
    public void Una_pagina_con_continuacion_trae_sus_mensajes_en_UTC_y_dice_que_hay_mas()
    {
        var pagina = Microsoft365GraphClient.LeerPaginaRecibidos(
            "{\"value\":[{\"id\":\"m1\",\"receivedDateTime\":\"2026-11-15T10:00:00Z\"},{\"id\":\"m2\",\"receivedDateTime\":\"2026-11-15T11:30:00+01:00\"}]," +
            "\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users/x/messages?$skip=1000\"}");

        pagina.Should().NotBeNull();
        pagina!.Mensajes.Select(m => m.MensajeExternoId).Should().Equal("m1", "m2");
        pagina.Mensajes[1].RecibidoUtc.Should().Be(new DateTime(2026, 11, 15, 10, 30, 0, DateTimeKind.Utc));
        pagina.HayMas.Should().BeTrue();
    }

    [Fact]
    public void Una_pagina_sin_continuacion_es_el_final()
    {
        var pagina = Microsoft365GraphClient.LeerPaginaRecibidos("{\"value\":[]}");

        pagina!.Mensajes.Should().BeEmpty();
        pagina.HayMas.Should().BeFalse();
    }

    [Fact]
    public async Task Cada_pagina_filtra_desde_la_fecha_en_orden_ascendente_con_paginas_de_mil_y_sin_skip()
    {
        var manejador = new Respuesta(HttpStatusCode.OK, "{\"value\":[]}");

        var resultado = await Cliente(manejador).ListarRecibidosDesdeAsync(
            "token", Compartido, new DateTime(2026, 11, 15, 9, 30, 15, 750, DateTimeKind.Utc), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var url = Uri.UnescapeDataString(manejador.Pedidas.Single().ToString());
        url.Should().StartWith("https://graph.microsoft.com/v1.0/users/cae@operador.test/mailFolders('Inbox')/messages?")
            .And.Contain("$filter=receivedDateTime ge 2026-11-15T09:30:15Z", "redondea hacia abajo al segundo")
            .And.Contain("$orderby=receivedDateTime asc")
            .And.Contain($"$top={Microsoft365GraphClient.TamanoPaginaRecuperacion}")
            .And.NotContain("$skip");
        Microsoft365GraphClient.TamanoPaginaRecuperacion.Should().Be(1000, "el máximo de Graph para messages");
    }

    [Fact]
    public async Task La_continuacion_de_una_pagina_nunca_se_pide()
    {
        var manejador = new Respuesta(HttpStatusCode.OK,
            "{\"value\":[{\"id\":\"m1\",\"receivedDateTime\":\"2026-11-15T10:00:00Z\"}]," +
            "\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users/x/messages?$skip=1000\"}");
        var cliente = Cliente(manejador);
        var desde = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

        var primera = await cliente.ListarRecibidosDesdeAsync("token", Compartido, desde, CancellationToken.None);
        await cliente.ListarRecibidosDesdeAsync("token", Compartido, desde, CancellationToken.None);

        primera.Valor.HayMas.Should().BeTrue();
        manejador.Pedidas.Should().HaveCount(2).And.OnlyContain(u => !u.ToString().Contains("skip", StringComparison.OrdinalIgnoreCase),
            "cada petición es una consulta por fecha nueva");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task Un_200_ilegible_es_un_fallo(string cuerpo)
    {
        var resultado = await Cliente(new Respuesta(HttpStatusCode.OK, cuerpo))
            .ListarRecibidosDesdeAsync("token", Compartido, DateTime.UtcNow, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, CodigosErrorMicrosoft365.Transitorio)]
    [InlineData(HttpStatusCode.BadRequest, "Integraciones.Microsoft365.ErrorApi")]
    [InlineData(HttpStatusCode.Unauthorized, "Integraciones.Microsoft365.ErrorApi")]
    public async Task Un_error_de_Graph_es_un_fallo(HttpStatusCode codigo, string esperado)
    {
        var resultado = await Cliente(new Respuesta(codigo, ""))
            .ListarRecibidosDesdeAsync("token", Compartido, DateTime.UtcNow, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(esperado);
    }

    [Fact]
    public void El_payload_de_recuperacion_lo_lee_la_ingesta()
    {
        var cliente = Cliente(new Respuesta(HttpStatusCode.OK, ""));

        var payload = cliente.ConstruirPayloadRecuperacion(["m1", "m2"]);

        cliente.ExtraerMensajeIdsDeNotificacion(payload).Should().Equal("m1", "m2");
    }
}
