using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using CaeManager.Infrastructure.Autenticacion;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace CaeManager.Web.Services;

/// <summary>
/// Reparto de cupos del limitador de la API pública (<c>/api/v1</c>, política <c>ApiPublica</c>).
///
/// <para>
/// <b>Qué corregía.</b> El limitador corre en <c>UseRateLimiter</c>, antes de <c>UseAuthorization</c>,
/// y el esquema por defecto es la cookie: el esquema <c>ApiKey</c> solo se ejecuta cuando la política
/// de autorización lo pide, es decir, después. Con la partición sacada de <c>HttpContext.User</c> el
/// principal que veía el limitador nunca era el de la clave, y todo el tráfico con clave (válida o
/// no) compartía un único cupo "sin-tenant": un anónimo con cabeceras basura podía dar 429 a las
/// integraciones de todos los Tenants.
/// </para>
///
/// <para>
/// <b>Diseño.</b> No se autentica antes de limitar: eso metería una consulta a base por cada petición
/// anónima antes de que ningún límite la frene (amplificación hacia la base), y las peticiones con
/// clave inválida seguirían necesitando un cupo propio. En su lugar, dos capas que no necesitan saber
/// si la clave es válida:
/// </para>
/// <list type="number">
/// <item><description><see cref="ParticionPorLlamante"/> (política <c>ApiPublica</c>): cupo por huella
/// de la clave presentada; sin cabecera <c>ApiKey</c>, cupo por IP del cliente. Dos llamantes
/// distintos no comparten cupo, y un anónimo solo agota el suyo.</description></item>
/// <item><description><see cref="ParticionGlobal"/> (limitador global, solo bajo <c>/api/v1</c>): techo
/// por IP, más holgado. Sin él, quien rote claves inventadas obtendría un cupo nuevo por cada una y
/// podría martillear la resolución de claves en base sin límite.</description></item>
/// </list>
///
/// <para>
/// La IP es la que deja <c>UseForwardedHeaders</c> (ver <c>CabecerasDeProxyDeBorde</c>), igual que
/// en el limitador de <c>/cuenta/*</c>. La clave nunca se guarda en claro: la partición lleva la
/// huella <see cref="CabeceraClaveApi.HuellaParaLimitador"/>, que ni siquiera coincide con el hash
/// persistido de la clave.
/// </para>
///
/// <para>
/// El token de una Clave de Integración del Tenant cuenta por el Id de la clave que lleva dentro, no por
/// el token: renovarlo no da un cupo nuevo. Leerlo descifra sin tocar la base y solo reparte el cupo, nunca
/// autoriza, igual que en <see cref="LimitadorEntregaExtension"/>. Un token ilegible cuenta por IP.
/// </para>
///
/// <para>
/// Consecuencias asumidas. (1) El cupo es por clave, no por Tenant; un Tenant con varias claves suma sus
/// cupos. Agrupar por Tenant exigiría autenticar antes de limitar, con el coste de arriba. (2) El techo
/// por IP se cobra antes de saber si la clave es válida: integraciones de Tenants distintos que salen por
/// la misma IP (una plataforma de integración, un NAT) comparten ese techo, que por eso es varias veces
/// el cupo por clave (<c>ApiPublica:RateLimitPorMinutoPorIp</c>). El middleware de limitación reintenta
/// el cupo global cuando la política rechaza, así que una petición rechazada gasta dos permisos de IP.
/// </para>
/// </summary>
public static class LimitadorApiPublica
{
    public const string Politica = "ApiPublica";

    /// <summary>Política de <c>POST /api/v1/token</c>: el secreto se usa poco, y un pico indica error o abuso.</summary>
    public const string PoliticaToken = "ApiPublicaToken";

    private const string PrefijoRuta = "/api/v1";

    /// <summary>Partición de la política <c>ApiPublica</c>: huella de la clave presentada, Id de la clave del token, o IP si no hay.</summary>
    public static RateLimitPartition<string> ParticionPorLlamante(
        HttpContext contexto, int limitePorMinuto, IDataProtectionProvider dataProtectionProvider)
    {
        var cabecera = contexto.Request.Headers.Authorization.ToString();
        var clave = CabeceraClaveApi.ExtraerClave(cabecera);
        if (!string.IsNullOrEmpty(clave))
            return VentanaFija($"clave:{CabeceraClaveApi.HuellaParaLimitador(clave)}", limitePorMinuto);

        var token = CabeceraClaveIntegracion.ExtraerToken(cabecera);
        var carga = string.IsNullOrEmpty(token) ? null : TokenClaveIntegracion.Leer(dataProtectionProvider, token);

        return VentanaFija(carga is null ? $"ip:{IpDelCliente(contexto)}" : $"integracion:{carga.ClaveId:N}", limitePorMinuto);
    }

    /// <summary>
    /// Partición de <see cref="PoliticaToken"/>: el Id de la clave presentada junto con la IP, o solo la IP si no
    /// hay. Por el Id y no por el secreto, que daría un cupo nuevo a cada secreto probado; con la IP, porque el Id
    /// no es secreto y por sí solo dejaría a cualquiera agotar el cupo de la integración ajena.
    /// </summary>
    public static RateLimitPartition<string> ParticionToken(HttpContext contexto, int limitePorMinuto)
    {
        var credenciales = CabeceraClaveIntegracion.ExtraerCredenciales(contexto.Request.Headers.Authorization.ToString());

        return VentanaFija(
            credenciales is null
                ? $"token-ip:{IpDelCliente(contexto)}"
                : $"token:{credenciales.ClaveId:N}:{IpDelCliente(contexto)}",
            limitePorMinuto);
    }

    /// <summary>
    /// Partición del limitador global para <c>/api/v1</c>: techo por IP. Devuelve <c>null</c> fuera de la
    /// API, para que el llamador siga con el resto de reglas del limitador global.
    /// </summary>
    public static RateLimitPartition<string>? ParticionGlobal(HttpContext contexto, int limitePorIpPorMinuto) =>
        contexto.Request.Path.StartsWithSegments(PrefijoRuta)
            ? VentanaFija($"api-ip:{IpDelCliente(contexto)}", limitePorIpPorMinuto)
            : null;

    /// <summary>
    /// IP del cliente como identificador de cupo. Una IPv6 se agrupa por su prefijo /64 (lo que se asigna a
    /// una sola conexión): sin ello, quien dispone de un /64 rotaría direcciones para obtener un techo nuevo
    /// en cada una. Una IPv4 mapeada en IPv6 cuenta como la IPv4.
    /// </summary>
    internal static string IpDelCliente(HttpContext contexto)
    {
        var ip = contexto.Connection.RemoteIpAddress;
        if (ip is null)
            return "desconocida";

        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
            return ip.ToString();

        Span<byte> octetos = stackalloc byte[16];
        ip.TryWriteBytes(octetos, out _);
        octetos[8..].Clear();
        return $"{new IPAddress(octetos)}/64";
    }

    private static RateLimitPartition<string> VentanaFija(string clavePartition, int limitePorMinuto) =>
        RateLimitPartition.GetFixedWindowLimiter(
            clavePartition,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limitePorMinuto,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
}
