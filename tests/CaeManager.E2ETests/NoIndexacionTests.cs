using System.Net;
using Xunit;

namespace CaeManager.E2ETests;

/// <summary>
/// La aplicación (app.talveg.es y staging.talveg.es sirven la misma imagen) no debe indexarse en
/// buscadores ni competir con la web pública de marketing. Contra la aplicación arrancada de verdad,
/// con su pipeline completo: toda respuesta —página, activo estático, health check, redirección al acceso y el propio
/// robots.txt— lleva <c>X-Robots-Tag: noindex, nofollow</c>, y robots.txt existe y no bloquea el
/// rastreo (si lo bloqueara, el buscador no descargaría la página y no llegaría a ver el noindex).
/// </summary>
[Collection("AppCollection")]
public class NoIndexacionTests(WebAppFixture fixture)
{
    [Theory]
    [InlineData("/cuenta/iniciar-sesion", HttpStatusCode.OK)]
    [InlineData("/robots.txt", HttpStatusCode.OK)]
    [InlineData("/favicon.png", HttpStatusCode.OK)]
    [InlineData("/salud", HttpStatusCode.OK)]
    // Sin sesión, una ruta que no existe no llega al 404: la FallbackPolicy redirige antes al acceso.
    // Esa redirección es también una respuesta que el buscador recibe.
    [InlineData("/ruta-que-no-existe-noindex", HttpStatusCode.Found)]
    public async Task Toda_respuesta_lleva_noindex_nofollow(string ruta, HttpStatusCode estadoEsperado)
    {
        using var cliente = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var respuesta = await cliente.GetAsync($"{fixture.BaseUrl}{ruta}");

        // Control positivo: lo medido es la respuesta esperada, no una redirección ni un error.
        Assert.Equal(estadoEsperado, respuesta.StatusCode);
        Assert.True(respuesta.Headers.TryGetValues("X-Robots-Tag", out var valores),
            $"{ruta} no trae X-Robots-Tag");
        Assert.Equal("noindex, nofollow", string.Join(", ", valores!));
    }

    [Fact]
    public async Task Robots_txt_permite_el_rastreo()
    {
        using var cliente = new HttpClient();
        var respuesta = await cliente.GetAsync($"{fixture.BaseUrl}/robots.txt");
        respuesta.EnsureSuccessStatusCode();

        Assert.StartsWith("text/plain", respuesta.Content.Headers.ContentType?.MediaType);
        var lineas = (await respuesta.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Contains("User-agent: *", lineas);
        Assert.Contains("Allow: /", lineas);
        Assert.DoesNotContain(lineas, linea => linea.StartsWith("Disallow:", StringComparison.OrdinalIgnoreCase)
                                               && linea.Trim().Length > "Disallow:".Length);
    }
}
