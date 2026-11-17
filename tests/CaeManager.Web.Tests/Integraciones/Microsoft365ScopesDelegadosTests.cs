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

    [Fact]
    public void La_lista_unica_que_usan_autorizacion_canje_y_refresco_incluye_User_Read()
    {
        Microsoft365GraphClient.ScopesDelegados.Should().Contain("https://graph.microsoft.com/User.Read");
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
