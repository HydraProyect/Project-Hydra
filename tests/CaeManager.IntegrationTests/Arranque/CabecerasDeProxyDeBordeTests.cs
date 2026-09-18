using System.Net;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// Qué hace de verdad la configuración de <c>UseForwardedHeaders</c> con la que
/// arranca la aplicación (REC-019).
///
/// <para>
/// <b>Qué se está midiendo.</b> Que la aplicación confía en el
/// <c>X-Forwarded-For</c> y el <c>X-Forwarded-Proto</c> de <i>cualquier</i>
/// remitente, porque <c>CabecerasDeProxyDeBorde</c> deja vacías
/// <c>KnownProxies</c> y <c>KnownIPNetworks</c>. Este test no denuncia un
/// defecto: fija por escrito el supuesto del que depende la autenticidad de la
/// IP del cliente, que es que el proxy de borde la sanee y sea el único camino
/// hasta el puerto de la aplicación. Si mañana alguien restringe esas listas,
/// este test se pone en rojo y obliga a decidirlo a propósito en vez de
/// descubrirlo cuando el login deje de funcionar.
/// </para>
///
/// <para>
/// <b>Por qué se ejercita el middleware real.</b> Comprobar la <i>forma</i> de
/// las opciones (que las listas están vacías) no demuestra el
/// <i>comportamiento</i>: la regla que importa vive dentro de
/// <c>ForwardedHeadersMiddleware</c>, que solo filtra por remitente cuando
/// alguna de las dos listas tiene elementos. Se ejecuta el middleware del
/// framework sobre un <c>HttpContext</c> real para medir la consecuencia, no la
/// intención.
/// </para>
/// </summary>
public class CabecerasDeProxyDeBordeTests
{
    private static async Task<HttpContext> EjecutarMiddlewareAsync(
        IPAddress remitente,
        Action<HttpContext> prepararPeticion)
    {
        var contexto = new DefaultHttpContext();
        contexto.Connection.RemoteIpAddress = remitente;
        contexto.Request.Scheme = "http";
        prepararPeticion(contexto);

        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(CabecerasDeProxyDeBorde.Opciones()));

        await middleware.Invoke(contexto);

        return contexto;
    }

    [Theory]
    // Un vecino cualquiera de la red interna, que es lo que de verdad puede
    // alcanzar el 8080 sin pasar por Caddy.
    [InlineData("172.20.0.9")]
    // Y una IP pública arbitraria, para que no quede duda de que tampoco hay un
    // filtro implícito por rango privado.
    [InlineData("203.0.113.9")]
    public async Task Un_remitente_cualquiera_impone_la_IP_del_cliente(string remitente)
    {
        var contexto = await EjecutarMiddlewareAsync(
            IPAddress.Parse(remitente),
            c => c.Request.Headers["X-Forwarded-For"] = "1.2.3.4");

        contexto.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("1.2.3.4"),
            "con KnownProxies y KnownIPNetworks vacíos el middleware no comprueba quién envía la cabecera, " +
            "así que la IP que llega al limitador de tasa es la que diga el remitente");
    }

    [Fact]
    public async Task Un_remitente_cualquiera_impone_tambien_el_esquema()
    {
        var contexto = await EjecutarMiddlewareAsync(
            IPAddress.Parse("172.20.0.9"),
            c => c.Request.Headers["X-Forwarded-Proto"] = "https");

        contexto.Request.Scheme.Should().Be("https",
            "es la otra cara del mismo supuesto, y es la que obliga a dejar las listas vacías: sin aceptar " +
            "X-Forwarded-Proto de Caddy —que no habla desde loopback— la aplicación generaría redirects " +
            "http:// y el login se rompería contra form-action de la CSP");
    }

    [Fact]
    public async Task Sin_cabecera_se_conserva_la_IP_de_la_conexion()
    {
        var contexto = await EjecutarMiddlewareAsync(
            IPAddress.Parse("172.20.0.9"),
            _ => { });

        contexto.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("172.20.0.9"),
            "control negativo: si esto cambiara sin cabecera, el test de arriba estaría midiendo otra cosa");
    }

    [Fact]
    public async Task Solo_se_consume_un_salto_de_la_cadena()
    {
        // ForwardLimit vale 1 por defecto y no se toca. Importa dejarlo escrito:
        // es lo que hace que, de una cadena, la aplicación se quede con el
        // ÚLTIMO elemento —el que añadiría el proxy más cercano— y no con el
        // primero, que es el que un cliente controlaría si el proxy hiciera
        // append en vez de reemplazar.
        var contexto = await EjecutarMiddlewareAsync(
            IPAddress.Parse("172.20.0.9"),
            c => c.Request.Headers["X-Forwarded-For"] = "1.2.3.4, 5.6.7.8");

        contexto.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("5.6.7.8"),
            "con ForwardLimit = 1 se consume un único salto, el de la derecha");
    }
}
