using System.Threading.RateLimiting;
using CaeManager.Infrastructure.Autenticacion;
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
/// Consecuencia asumida: el cupo es por clave, no por Tenant; un Tenant con varias claves suma sus
/// cupos. Agrupar por Tenant exigiría autenticar antes de limitar, con el coste de arriba.
/// </para>
/// </summary>
public static class LimitadorApiPublica
{
    public const string Politica = "ApiPublica";

    private const string PrefijoRuta = "/api/v1";

    /// <summary>Partición de la política <c>ApiPublica</c>: huella de la clave presentada, o IP si no hay.</summary>
    public static RateLimitPartition<string> ParticionPorLlamante(HttpContext contexto, int limitePorMinuto)
    {
        var clave = CabeceraClaveApi.ExtraerClave(contexto.Request.Headers.Authorization.ToString());

        var particion = string.IsNullOrEmpty(clave)
            ? $"ip:{IpDelCliente(contexto)}"
            : $"clave:{CabeceraClaveApi.HuellaParaLimitador(clave)}";

        return VentanaFija(particion, limitePorMinuto);
    }

    /// <summary>
    /// Partición del limitador global para <c>/api/v1</c>: techo por IP. Devuelve <c>null</c> fuera de la
    /// API, para que el llamador siga con el resto de reglas del limitador global.
    /// </summary>
    public static RateLimitPartition<string>? ParticionGlobal(HttpContext contexto, int limitePorIpPorMinuto) =>
        contexto.Request.Path.StartsWithSegments(PrefijoRuta)
            ? VentanaFija($"api-ip:{IpDelCliente(contexto)}", limitePorIpPorMinuto)
            : null;

    private static string IpDelCliente(HttpContext contexto) =>
        contexto.Connection.RemoteIpAddress?.ToString() ?? "desconocida";

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
