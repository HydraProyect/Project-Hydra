using CaeManager.Application.AltaAutonoma;
using CaeManager.Web.Features.AltaAutonoma.Registro;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace CaeManager.Web.Tests.AltaAutonoma;

/// <summary>
/// Atributos de la cookie del credencial de continuación (A4), leídos de la cabecera <c>Set-Cookie</c> que sale de verdad:
/// HttpOnly, SameSite=Strict, Path=/alta, Secure siempre y dos horas de vida; el borrado, con el mismo Path.
/// </summary>
public class CookieContinuacionAltaTests
{
    [Fact]
    public void Escribir_fija_HttpOnly_SameSite_Strict_Path_alta_Secure_y_dos_horas()
    {
        var http = new DefaultHttpContext();
        http.Request.IsHttps = true;
        var credencial = ServicioRegistroAlta.NuevoToken();

        CookieContinuacionAlta.Escribir(http, credencial);

        var cookie = Unica(http);
        cookie.Name.Value.Should().Be(CookieContinuacionAlta.Nombre);
        cookie.Value.Value.Should().Be(credencial);
        cookie.HttpOnly.Should().BeTrue();
        cookie.SameSite.Should().Be(Microsoft.Net.Http.Headers.SameSiteMode.Strict);
        cookie.Path.Value.Should().Be("/alta");
        cookie.Secure.Should().BeTrue();
        cookie.MaxAge.Should().Be(TimeSpan.FromHours(2));
        cookie.Domain.HasValue.Should().BeFalse("solo la recibe el host que la puso");
    }

    /// <summary>
    /// Revisión de A4: <c>Secure</c> no depende de <c>IsHttps</c>. Si detrás del proxy el esquema llegara mal, el
    /// credencial seguiría sin poder viajar por HTTP en claro. En local se prueba con el perfil HTTPS de Kestrel.
    /// </summary>
    [Fact]
    public void Sin_HTTPS_tambien_lleva_Secure()
    {
        var http = new DefaultHttpContext();
        http.Request.IsHttps.Should().BeFalse("guarda del instrumento");

        CookieContinuacionAlta.Escribir(http, ServicioRegistroAlta.NuevoToken());

        Unica(http).Secure.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Borrar_caduca_la_cookie_en_el_mismo_Path_y_con_los_mismos_atributos(bool https)
    {
        var http = new DefaultHttpContext();
        http.Request.IsHttps = https;

        CookieContinuacionAlta.Borrar(http);

        var cookie = Unica(http);
        cookie.Name.Value.Should().Be(CookieContinuacionAlta.Nombre);
        cookie.Value.Value.Should().BeEmpty();
        cookie.Path.Value.Should().Be("/alta", "con otro Path el navegador no borraría la de /alta");
        cookie.Expires.Should().NotBeNull();
        cookie.Expires!.Value.Should().BeBefore(DateTimeOffset.UtcNow);
        cookie.HttpOnly.Should().BeTrue();
        cookie.SameSite.Should().Be(Microsoft.Net.Http.Headers.SameSiteMode.Strict);
        cookie.Secure.Should().BeTrue();
    }

    private static SetCookieHeaderValue Unica(DefaultHttpContext http)
    {
        var cabeceras = http.Response.Headers.SetCookie;
        cabeceras.Should().ContainSingle();
        return SetCookieHeaderValue.Parse(cabeceras[0]);
    }
}
