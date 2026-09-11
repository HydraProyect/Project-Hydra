using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace CaeManager.E2ETests;

/// <summary>
/// El hash sha256 que CabecerasSeguridadExtensions pone en <c>script-src</c>
/// tiene que ser, byte a byte (tras normalizar saltos de línea a \n, igual
/// que hace el parser HTML de cualquier navegador antes de calcular el hash
/// de un &lt;script&gt; inline), el del &lt;script type="importmap"&gt; que la
/// página realmente sirve. Si diverge, la CSP bloquea el importmap en TODA
/// navegación — visto de verdad: un hash fijado a mano en el código quedaba
/// obsoleto en cuanto cambiaba el árbol de activos JS, y por separado, el
/// checkout de Windows (CRLF) medía un hash distinto al que sirve
/// producción (LF). Este test no fija ningún hash: sirve una página real,
/// mide el importmap que llegó de verdad y compara contra la cabecera que
/// llegó en la misma respuesta — así que sigue siendo válido pase lo que
/// pase con los módulos JS de la app.
/// </summary>
[Collection("AppCollection")]
public class ImportMapCspHashTests(WebAppFixture fixture)
{
    [Fact]
    public async Task El_hash_de_script_src_coincide_con_el_importmap_servido_en_la_misma_pagina()
    {
        using var cliente = new HttpClient();
        var respuesta = await cliente.GetAsync($"{fixture.BaseUrl}/cuenta/iniciar-sesion");
        respuesta.EnsureSuccessStatusCode();

        var html = await respuesta.Content.ReadAsStringAsync();
        var importMap = ExtraerImportMap(html);

        var hashEsperado = HashSha256Base64(NormalizarSaltosDeLinea(importMap));
        var hashEnCabecera = ExtraerHashScriptSrc(respuesta);

        Assert.Equal(hashEsperado, hashEnCabecera);
    }

    private static string ExtraerImportMap(string html)
    {
        var coincidencia = Regex.Match(html, "<script type=\"importmap\">(.*?)</script>", RegexOptions.Singleline);
        Assert.True(coincidencia.Success,
            "La página no sirvió ningún <script type=\"importmap\">: ¿se quitó <ImportMap /> de App.razor?");
        return coincidencia.Groups[1].Value;
    }

    private static string ExtraerHashScriptSrc(HttpResponseMessage respuesta)
    {
        var valoresCsp = respuesta.Headers.GetValues("Content-Security-Policy");
        var directivaScriptSrc = valoresCsp.FirstOrDefault(valor => valor.Contains("script-src", StringComparison.Ordinal));
        Assert.True(directivaScriptSrc is not null,
            $"Ninguna de las cabeceras Content-Security-Policy trae script-src: {string.Join(" | ", valoresCsp)}");

        var coincidencia = Regex.Match(directivaScriptSrc!, "script-src[^;]*'sha256-([^']+)'");
        Assert.True(coincidencia.Success,
            $"script-src no trae ningún hash sha256 (¿CSP debilitada a 'unsafe-inline'?): {directivaScriptSrc}");
        return coincidencia.Groups[1].Value;
    }

    private static string NormalizarSaltosDeLinea(string texto) =>
        texto.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string HashSha256Base64(string texto) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(texto)));
}
