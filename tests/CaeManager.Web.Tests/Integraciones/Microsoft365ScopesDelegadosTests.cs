using System.Net;
using System.Text;
using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>
/// El alta del buzón propio lee <c>/me</c> con el token delegado; sin <c>User.Read</c> en el consentimiento Graph responde 403 y
/// el callback acaba en «autenticación». Sin red: se inspecciona la URL de autorización y la petición a <c>/me</c>.
/// </summary>
public class Microsoft365ScopesDelegadosTests
{
    private sealed class AnotaUrlHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"mail":"gestora@talveg.example"}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static Microsoft365GraphClient Cliente(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Options.Create(new Microsoft365GraphOptions()), NullLogger<Microsoft365GraphClient>.Instance);

    [Fact]
    public void La_url_de_autorizacion_pide_User_Read_ademas_de_Mail_Read_Mail_Send_y_offline_access()
    {
        var url = Cliente(new AnotaUrlHandler()).ConstruirUrlAutorizacion("https://talveg.example/integraciones/microsoft365-callback", "estado");

        var scope = Uri.UnescapeDataString(url.Split("scope=")[1].Split('&')[0]).Split(' ');
        scope.Should().BeEquivalentTo(
        [
            "offline_access",
            "https://graph.microsoft.com/User.Read",
            "https://graph.microsoft.com/Mail.Read",
            "https://graph.microsoft.com/Mail.Send",
        ]);
    }

    private sealed class CapturaFormularioHandler : HttpMessageHandler
    {
        public string? Cuerpo { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Cuerpo = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"a","refresh_token":"r","expires_in":3600}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (Microsoft365GraphClient Cliente, CapturaFormularioHandler Handler) ClienteConSecreto()
    {
        var handler = new CapturaFormularioHandler();
        var opciones = new Microsoft365GraphOptions { ClientId = "cliente", ClientSecret = "secreto", UrlPublicaBase = "https://talveg.example" };
        return (new Microsoft365GraphClient(new HttpClient(handler), Options.Create(opciones), NullLogger<Microsoft365GraphClient>.Instance), handler);
    }

    [Fact]
    public async Task El_canje_del_codigo_pide_los_mismos_scopes_que_la_autorizacion_con_User_Read()
    {
        var (cliente, handler) = ClienteConSecreto();

        await cliente.IntercambiarCodigoPorTokensAsync("codigo", "https://talveg.example/cb", CancellationToken.None);

        System.Web.HttpUtility.ParseQueryString(handler.Cuerpo!)["scope"].Should().Be(Microsoft365GraphClient.ScopesDelegados)
            .And.Contain("https://graph.microsoft.com/User.Read");
    }

    [Fact]
    public async Task El_refresco_no_pide_User_Read_para_no_exceder_lo_concedido_a_las_conexiones_ya_guardadas()
    {
        var (cliente, handler) = ClienteConSecreto();

        await cliente.RefrescarTokensAsync("refresh", CancellationToken.None);

        var scope = System.Web.HttpUtility.ParseQueryString(handler.Cuerpo!)["scope"];
        scope.Should().Be(Microsoft365GraphClient.ScopesRefresco).And.NotContain("User.Read");
        scope.Should().Contain("offline_access").And.Contain("Mail.Read").And.Contain("Mail.Send");
    }

    [Fact]
    public async Task La_lectura_del_buzon_propio_pide_solo_mail_y_userPrincipalName_de_me()
    {
        var handler = new AnotaUrlHandler();

        var resultado = await Cliente(handler).ObtenerBuzonEmailAsync("token", CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        handler.Urls.Should().ContainSingle().Which.Should().Be("https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName");
    }
}
