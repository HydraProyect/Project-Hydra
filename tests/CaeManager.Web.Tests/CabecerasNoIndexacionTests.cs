using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La aplicación (app.talveg.es y staging.talveg.es) no se indexa: <c>UseCabecerasSeguridad</c> pone
/// <c>X-Robots-Tag: noindex, nofollow</c> en toda respuesta, también en las que no son HTML y en las de
/// error. Aquí se prueba el middleware aislado; la composición con el pipeline real (estáticos, 404,
/// <c>robots.txt</c>) la prueba <c>NoIndexacionTests</c> en E2E.
/// </summary>
public sealed class CabecerasNoIndexacionTests
{
    [Theory]
    [InlineData("/cuenta/iniciar-sesion", StatusCodes.Status200OK, "text/html")]
    [InlineData("/documentos/1/archivo", StatusCodes.Status200OK, "application/pdf")]
    [InlineData("/no-existe", StatusCodes.Status404NotFound, "text/plain")]
    public async Task Toda_respuesta_lleva_noindex_nofollow(string ruta, int estado, string tipo)
    {
        var contexto = new DefaultHttpContext();
        contexto.Request.Path = ruta;
        contexto.Response.Headers.ContainsKey("X-Robots-Tag").Should().BeFalse(
            "el punto de partida es una respuesta sin la cabecera: lo medido lo aporta el middleware");

        await EjecutarAsync(contexto, c =>
        {
            c.Response.StatusCode = estado;
            c.Response.ContentType = tipo;
            return Task.CompletedTask;
        });

        contexto.Response.Headers["X-Robots-Tag"].ToString().Should().Be("noindex, nofollow");
    }

    private static async Task EjecutarAsync(HttpContext contexto, RequestDelegate terminal)
    {
        var servicios = new ServiceCollection().AddLogging().BuildServiceProvider();
        var app = new ApplicationBuilder(servicios);
        app.UseCabecerasSeguridad();
        app.Run(terminal);
        contexto.RequestServices = servicios;
        await app.Build()(contexto);
    }
}
