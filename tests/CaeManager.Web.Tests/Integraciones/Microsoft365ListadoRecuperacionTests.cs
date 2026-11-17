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

    private static readonly DateTime Segundo = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

    private static string PaginaDe(params (string Id, string Recibido)[] mensajes) =>
        "{\"value\":[" + string.Join(",", mensajes.Select(m => $"{{\"id\":\"{m.Id}\",\"receivedDateTime\":\"{m.Recibido}\"}}")) + "]}";

    /// <summary>Issue #2263: el recorrido por Id de un segundo pide eq el segundo y id gt el desempate, en orden de Id.</summary>
    [Fact]
    public async Task El_recorrido_por_id_filtra_el_segundo_exacto_y_los_ids_posteriores_en_orden_de_id()
    {
        var manejador = new Respuesta(HttpStatusCode.OK, "{\"value\":[]}");
        var cliente = Cliente(manejador);

        await cliente.ListarRecibidosEnSegundoAsync("token", Compartido, Segundo, null, CancellationToken.None);
        await cliente.ListarRecibidosEnSegundoAsync("token", Compartido, Segundo, "AAMk+/='x", CancellationToken.None);

        var primera = Uri.UnescapeDataString(manejador.Pedidas[0].ToString());
        primera.Should().StartWith("https://graph.microsoft.com/v1.0/users/cae@operador.test/mailFolders('Inbox')/messages?")
            .And.Contain("$filter=receivedDateTime eq 2026-11-15T10:00:00Z and id gt ''&",
                "las dos propiedades del $orderby también en el $filter, o Graph responde InefficientFilter")
            .And.Contain("$orderby=receivedDateTime asc,id asc")
            .And.Contain($"$top={Microsoft365GraphClient.TamanoPaginaRecuperacion}")
            .And.NotContain("$skip");
        Uri.UnescapeDataString(manejador.Pedidas[1].ToString())
            .Should().Contain("$filter=receivedDateTime eq 2026-11-15T10:00:00Z and id gt 'AAMk+/=''x'&", "la comilla del Id se dobla");
        manejador.Pedidas[1].AbsoluteUri.Should().Contain("AAMk%2B%2F%3D", "el Id va escapado en la URL");
    }

    [Fact]
    public async Task El_recorrido_por_id_no_admite_un_segundo_con_fraccion()
    {
        var cliente = Cliente(new Respuesta(HttpStatusCode.OK, "{\"value\":[]}"));

        var pedir = () => cliente.ListarRecibidosEnSegundoAsync("token", Compartido, Segundo.AddMilliseconds(1), null, CancellationToken.None);

        await pedir.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Una_pagina_del_segundo_en_orden_de_id_es_legible()
    {
        var pagina = Microsoft365GraphClient.LeerPaginaRecibidosEnSegundo(
            PaginaDe(("b", "2026-11-15T10:00:00Z"), ("c", "2026-11-15T10:00:00Z")), Segundo, "a");

        pagina!.Mensajes.Select(m => m.MensajeExternoId).Should().Equal("b", "c");
    }

    /// <summary>
    /// Avanzar el desempate al último Id de la página solo es seguro si Graph aplicó el filtro y el orden pedidos con la
    /// misma comparación que el desempate: si no, la página es ilegible y la recuperación falla cerrado.
    /// </summary>
    [Theory]
    [InlineData("b", "2026-11-15T10:00:01Z", "c", "2026-11-15T10:00:00Z", null)]
    [InlineData("b", "2026-11-15T09:59:59Z", "c", "2026-11-15T10:00:00Z", null)]
    [InlineData("c", "2026-11-15T10:00:00Z", "b", "2026-11-15T10:00:00Z", null)]
    [InlineData("b", "2026-11-15T10:00:00Z", "b", "2026-11-15T10:00:00Z", null)]
    [InlineData("a", "2026-11-15T10:00:00Z", "B", "2026-11-15T10:00:00Z", null)]
    [InlineData("a", "2026-11-15T10:00:00Z", "b", "2026-11-15T10:00:00Z", "a")]
    [InlineData("b", "2026-11-15T10:00:00Z", "c|d", "2026-11-15T10:00:00Z", "a")]
    public void Una_pagina_del_segundo_fuera_del_filtro_o_del_orden_es_ilegible(
        string primero, string recibidoPrimero, string segundo, string recibidoSegundo, string? despuesDe) =>
        Microsoft365GraphClient.LeerPaginaRecibidosEnSegundo(
            PaginaDe((primero, recibidoPrimero), (segundo, recibidoSegundo)), Segundo, despuesDe).Should().BeNull();

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
