using System.Security.Claims;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace CaeManager.Web.Tests;

public class TemaCookieTests
{
    [Theory]
    [InlineData("claro")]
    [InlineData("oscuro")]
    public void Devuelve_el_valor_de_la_cookie_cuando_hay_sesion_autenticada(string valor)
    {
        var servicio = new TemaCookie(new HttpContextAccessorFalso(autenticado: true, cookieTema: valor));

        servicio.TemaAplicado.Should().Be(valor);
    }

    [Fact]
    public void No_hay_sesion_autenticada_nunca_devuelve_la_cookie_aunque_exista()
    {
        var servicio = new TemaCookie(new HttpContextAccessorFalso(autenticado: false, cookieTema: "oscuro"));

        servicio.TemaAplicado.Should().BeNull();
    }

    [Fact]
    public void Sin_cookie_devuelve_null()
    {
        var servicio = new TemaCookie(new HttpContextAccessorFalso(autenticado: true, cookieTema: null));

        servicio.TemaAplicado.Should().BeNull();
    }

    /// <summary>
    /// "sistema" nunca debe llevar data-theme (ver tokens.css: el
    /// auto-seguimiento de prefers-color-scheme está desactivado a
    /// propósito). Un valor manipulado o corrupto tampoco.
    /// </summary>
    [Theory]
    [InlineData("sistema")]
    [InlineData("")]
    [InlineData("Oscuro")]
    [InlineData("<script>")]
    public void Un_valor_que_no_sea_claro_u_oscuro_se_trata_como_ausente(string valor)
    {
        var servicio = new TemaCookie(new HttpContextAccessorFalso(autenticado: true, cookieTema: valor));

        servicio.TemaAplicado.Should().BeNull();
    }

    [Fact]
    public void Sin_HttpContext_devuelve_null()
    {
        var servicio = new TemaCookie(new HttpContextAccessorFalso(httpContext: null));

        servicio.TemaAplicado.Should().BeNull();
    }

    private sealed class HttpContextAccessorFalso : IHttpContextAccessor
    {
        private readonly HttpContext? _httpContext;

        public HttpContextAccessorFalso(bool autenticado, string? cookieTema)
        {
            var contexto = new DefaultHttpContext();
            contexto.User = autenticado
                ? new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "prueba"))
                : new ClaimsPrincipal(new ClaimsIdentity());

            if (cookieTema is not null)
                contexto.Request.Headers.Append("Cookie", $"{TemaCookie.NombreCookie}={cookieTema}");

            _httpContext = contexto;
        }

        public HttpContextAccessorFalso(HttpContext? httpContext) => _httpContext = httpContext;

        public HttpContext? HttpContext
        {
            get => _httpContext;
            set => throw new NotSupportedException();
        }
    }
}
