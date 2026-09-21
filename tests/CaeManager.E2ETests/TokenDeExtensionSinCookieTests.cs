using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Regresión de un hallazgo real del MVP1 de extensión de navegador (ver
/// ARQUITECTURA-INTEGRACIONES.md § 14 en el repositorio de negocio): ninguna
/// de las suites existentes (unitarios con fakes, integración con
/// <c>ExtensionAuthenticationHandler</c> construido a mano) ejercita el
/// pipeline HTTP real completo, así que ninguna detectó que
/// <c>TenantActual</c> devolvía el tenant correcto cuando la petición traía
/// cookie de sesión y <c>null</c> cuando solo traía el token de la extensión
/// — que es la situación real de la extensión, que nunca tiene cookie de
/// Hydra. Se vio primero a mano, contra un servidor local real, con
/// <c>fetch(..., {credentials: 'omit'})</c>.
///
/// <para>
/// Este test reproduce exactamente esa condición con <see cref="HttpClient"/>
/// puro (nunca envía cookies) y compara su resultado contra el mismo token
/// usado a través de una petición que SÍ lleva la cookie de la pestaña de
/// Playwright — si algún día <c>TenantActual</c> volviera a cachear su
/// resolución antes de tiempo, las dos respuestas divergerían otra vez (la de
/// sin-cookie a vacío) y este test lo notaría. No depende de qué acreditación
/// exacta haya sembrada -- solo de que las dos vías vean lo mismo -- porque
/// "AppCollection" es compartida con el resto de la suite E2E.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class TokenDeExtensionSinCookieTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Un_token_de_extension_sin_cookie_ve_lo_mismo_que_con_cookie()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/cuenta/extension");
        await page.GetByRole(AriaRole.Button, new() { Name = "Generar token" }).ClickAsync();

        // La pantalla ya no enseña el token en crudo: enseña el CÓDIGO DE
        // CONEXIÓN, que lo lleva dentro junto al origen y la caducidad. El test
        // hacía `page.Locator(".campo input")` —«el primer campo que haya»— y por
        // eso al cambiar la pantalla siguió encontrando un input, leyó el código
        // y lo mandó como si fuera un token; el servidor respondió con la página
        // de login y el fallo no se parecía en nada a su causa. Ahora se busca
        // por su etiqueta, que es lo que de verdad identifica al campo.
        var campoCodigo = page.GetByLabel("Código de conexión");
        await Assertions.Expect(campoCodigo).Not.ToHaveValueAsync(
            string.Empty, new LocatorAssertionsToHaveValueOptions { Timeout = 15_000 });
        var token = TokenDelCodigoDeConexion(await campoCodigo.InputValueAsync());

        var cookiesDeLaSesion = await contexto.CookiesAsync();
        var cabeceraCookie = string.Join("; ", cookiesDeLaSesion.Select(c => $"{c.Name}={c.Value}"));

        // Sin cookie en absoluto — la condición real de la extensión, que solo
        // conoce el token. Un HttpClient nuevo, nunca el de Playwright, para
        // no arrastrar ninguna cookie de la pestaña sin darse cuenta.
        using var clienteSinCookie = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) };
        using var peticionSinCookie = new HttpRequestMessage(HttpMethod.Get, "/extension/acreditaciones-pendientes");
        peticionSinCookie.Headers.Authorization = new AuthenticationHeaderValue("Extension", token);
        var respuestaSinCookie = await clienteSinCookie.SendAsync(peticionSinCookie);
        respuestaSinCookie.EnsureSuccessStatusCode();
        var cuerpoSinCookie = await respuestaSinCookie.Content.ReadAsStringAsync();

        // Mismo token, pero con la cookie de la sesión añadida a mano —
        // reproduce lo que ya se sabía correcto (probado a mano) para tener
        // con qué comparar.
        using var clienteConCookie = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) };
        using var peticionConCookie = new HttpRequestMessage(HttpMethod.Get, "/extension/acreditaciones-pendientes");
        peticionConCookie.Headers.Authorization = new AuthenticationHeaderValue("Extension", token);
        peticionConCookie.Headers.Add("Cookie", cabeceraCookie);
        var respuestaConCookie = await clienteConCookie.SendAsync(peticionConCookie);
        respuestaConCookie.EnsureSuccessStatusCode();
        var cuerpoConCookie = await respuestaConCookie.Content.ReadAsStringAsync();

        // Precondición: si la siembra de Refrielectric alguna vez deja de
        // tener acreditaciones pendientes, este test debe fallar diciendo
        // ESO, no confundirse con dos respuestas vacías "iguales" que no
        // demuestran nada.
        Assert.NotEqual("[]", cuerpoConCookie);

        // El token de extensión por sí solo debe ver exactamente la misma
        // cartera que la sesión interactiva — si TenantActual volviera a
        // cachear su resolución antes de que el esquema de extensión
        // autenticara, esta respuesta volvería a llegar vacía.
        Assert.Equal(cuerpoConCookie, cuerpoSinCookie);
    }

    /// <summary>
    /// Saca el token del código de conexión: base64 de <c>{u, t, e}</c>, el mismo
    /// contrato que lee <c>leerCodigoConexion</c> de <c>extension/background.js</c>
    /// y que produce <c>CodigoConexionExtension</c>.
    ///
    /// <para>
    /// Que este test tenga que decodificarlo no es un rodeo: es la única prueba
    /// de la suite que comprueba que el token empaquetado <b>lo acepta el
    /// servidor de verdad</b>. Las unitarias fijan el formato y el guion de Node
    /// comprueba que JavaScript sabe leerlo, pero ninguna de las dos puede
    /// distinguir un token bien empaquetado de uno bien empaquetado y caducado,
    /// mal firmado o de otro usuario.
    /// </para>
    /// </summary>
    private static string TokenDelCodigoDeConexion(string codigo)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(codigo));
        using var documento = JsonDocument.Parse(json);

        // Si el contrato cambiara de campo, esto tiene que fallar diciendo ESO y
        // no acabar mandando una cadena vacía como token, que daría el mismo 302
        // a login por un motivo completamente distinto.
        //
        // El mensaje lleva los NOMBRES de los campos, nunca el JSON entero: ahí
        // dentro va un token, y este repositorio es público, así que la salida de
        // un fallo de CI también lo es. Se comprobó midiendo — la primera versión
        // de esta línea imprimía el cuerpo completo y el token apareció entero en
        // la consola al provocar el fallo a propósito.
        var campos = string.Join(", ", documento.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.True(
            documento.RootElement.TryGetProperty("t", out var token),
            $"El código de conexión no trae el campo 't'. Campos presentes: {campos}.");

        var valor = token.GetString();
        Assert.False(string.IsNullOrWhiteSpace(valor), "El código de conexión trae 't' vacío.");

        return valor!;
    }
}
