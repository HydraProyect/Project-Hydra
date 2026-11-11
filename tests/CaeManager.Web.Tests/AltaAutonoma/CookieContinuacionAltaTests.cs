using CaeManager.Application.AltaAutonoma;
using CaeManager.Web.Features.AltaAutonoma.Registro;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace CaeManager.Web.Tests.AltaAutonoma;

/// <summary>
/// Atributos de la cookie del credencial de continuación (A4), leídos de la cabecera <c>Set-Cookie</c> que sale de verdad:
/// HttpOnly, SameSite=Strict, Path=/alta, Secure con HTTPS y dos horas de vida; el borrado, con el mismo Path.
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

    [Fact]
    public void Sin_HTTPS_no_lleva_Secure_para_no_perderse_en_local()
    {
        var http = new DefaultHttpContext();

        CookieContinuacionAlta.Escribir(http, ServicioRegistroAlta.NuevoToken());

        Unica(http).Secure.Should().BeFalse();
    }

    [Fact]
    public void Borrar_caduca_la_cookie_en_el_mismo_Path_y_con_los_mismos_atributos()
    {
        var http = new DefaultHttpContext();
        http.Request.IsHttps = true;

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
