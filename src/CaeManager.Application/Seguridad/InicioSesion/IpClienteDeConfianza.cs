using System.Net;
using System.Net.Sockets;

namespace CaeManager.Application.Seguridad.InicioSesion;

/// <summary>
/// IP del cliente para el registro de inicios de sesión.
///
/// <para>
/// <b>La cabecera nunca se cree tal cual.</b> <c>X-Forwarded-For</c> la puede
/// escribir cualquiera; solo dice algo cuando quien la entrega —el par de la
/// conexión TCP— es un proxy de confianza configurado. Entonces se recorre de
/// derecha a izquierda (cada proxy añade al final lo que vio) y la IP del
/// cliente es la primera que <b>no</b> es de confianza: lo que un cliente
/// escriba a la izquierda queda detrás de esa entrada y no se llega a leer. Si
/// el par no es de confianza, la cabecera se ignora y la IP es la del par.
/// </para>
///
/// <para>
/// No usa <c>HttpContext.Connection.RemoteIpAddress</c> después de
/// <c>UseForwardedHeaders</c>: ese middleware acepta la cabecera de cualquier
/// origen (ver <c>CabecerasDeProxyDeBorde</c>) y ya la habría sustituido. El par
/// y la cabecera originales los captura <c>CapturaOrigenConexion</c> antes.
/// </para>
/// </summary>
public static class IpClienteDeConfianza
{
    public static IPAddress? Resolver(
        IPAddress? parConexion, string? cabeceraXForwardedFor, IReadOnlyCollection<IPNetwork> proxiesDeConfianza)
    {
        if (parConexion is null)
            return null;

        var candidata = Normalizar(parConexion);
        if (!EsDeConfianza(candidata, proxiesDeConfianza) || string.IsNullOrWhiteSpace(cabeceraXForwardedFor))
            return candidata;

        var saltos = cabeceraXForwardedFor.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var i = saltos.Length - 1; i >= 0; i--)
        {
            // Una entrada ilegible corta el recorrido: lo que hay a su izquierda
            // no lo añadió ningún proxy de confianza que sepamos leer.
            if (!IntentarLeer(saltos[i], out var ip))
                return candidata;

            candidata = ip;
            if (!EsDeConfianza(ip, proxiesDeConfianza))
                return ip;
        }

        return candidata;
    }

    /// <summary>Lee la lista configurada («10.0.0.0/8», «::1/128», «203.0.113.7»); una entrada ilegible es un error de configuración.</summary>
    public static IReadOnlyList<IPNetwork> LeerRedes(IEnumerable<string>? redes)
    {
        var resultado = new List<IPNetwork>();
        foreach (var texto in redes ?? [])
        {
            if (string.IsNullOrWhiteSpace(texto))
                continue;
            var t = texto.Trim();
            if (!t.Contains('/'))
            {
                var ip = IPAddress.Parse(t);
                t = $"{t}/{(ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)}";
            }
            resultado.Add(IPNetwork.Parse(t));
        }
        return resultado;
    }

    /// <summary>IP privada, de loopback o de enlace local: la base de geolocalización no la ubica.</summary>
    public static bool EsNoPublica(IPAddress ip)
    {
        ip = Normalizar(ip);
        if (IPAddress.IsLoopback(ip))
            return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;

        var b = ip.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254)
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
               || b[0] == 0;
    }

    private static bool EsDeConfianza(IPAddress ip, IReadOnlyCollection<IPNetwork> redes)
        => redes.Any(r => r.Contains(ip));

    private static IPAddress Normalizar(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    /// <summary>Acepta «IP», «IPv4:puerto» y «[IPv6]:puerto».</summary>
    private static bool IntentarLeer(string texto, out IPAddress ip)
    {
        var t = texto.Trim().Trim('"');
        if (t.StartsWith('['))
        {
            var cierre = t.IndexOf(']');
            if (cierre > 0)
                t = t[1..cierre];
        }
        else if (t.Count(c => c == ':') == 1)
        {
            t = t[..t.IndexOf(':')];
        }

        if (IPAddress.TryParse(t, out var leida))
        {
            ip = Normalizar(leida);
            return true;
        }

        ip = IPAddress.None;
        return false;
    }
}
