using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// La URL de acceso de un canal es texto libre tecleado por un Gestor CAE. Solo
/// un destino http(s) puede acabar en un <c>href</c>: cualquier otro esquema
/// ejecutaría script o navegaría a un recurso que no es un portal.
/// </summary>
public class UrlPortalCaeTests
{
    [Theory]
    [InlineData("https://app.twind.io/login", "https://app.twind.io/login")]
    [InlineData("http://portal.ejemplo.es/", "http://portal.ejemplo.es/")]
    [InlineData("  https://nalanda.com  ", "https://nalanda.com/")]
    [InlineData("app.twind.io/login", "https://app.twind.io/login")]
    [InlineData("nalanda.com", "https://nalanda.com/")]
    public void Un_portal_http_o_sin_esquema_se_normaliza_a_una_url_absoluta_http_s(string entrada, string esperado) =>
        UrlPortalCae.Normalizar(entrada).Should().Be(esperado);

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://portal.ejemplo.es/")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("https://")]
    public void Lo_que_no_es_un_portal_http_s_no_da_destino(string? entrada) =>
        UrlPortalCae.Normalizar(entrada).Should().BeNull();
}
