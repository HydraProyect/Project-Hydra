using Microsoft.AspNetCore.HttpOverrides;

namespace CaeManager.Web.Services;

/// <summary>
/// Configuración de <c>UseForwardedHeaders</c> para el despliegue detrás del
/// proxy de borde (Caddy, ver <c>deploy/local/Caddyfile</c>).
///
/// <para>
/// <b>Qué decide esta función.</b> Que la aplicación acepte
/// <c>X-Forwarded-For</c> y <c>X-Forwarded-Proto</c> <i>de cualquier origen</i>,
/// porque <c>KnownProxies</c> y <c>KnownIPNetworks</c> quedan vacíos. En
/// ASP.NET Core la comprobación de origen solo se activa cuando alguna de las
/// dos listas tiene elementos; vacías ambas, el middleware no filtra por
/// remitente. No es un descuido: es el supuesto del que depende la seguridad
/// de la IP del cliente, y por eso se escribe aquí, en una función pura que un
/// test puede observar, en vez de quedar sepultado en el pipeline de
/// <c>Program.cs</c> (mismo motivo que <c>ResolverCadenaDeTrafico</c>).
/// </para>
///
/// <para>
/// <b>Por qué el Clear() explícito.</b> Las dos listas traen loopback por
/// defecto, y un inicializador <c>= { }</c> no las vacía: solo no añade nada
/// más. Sin este <c>Clear()</c> el middleware descartaría las cabeceras porque
/// Caddy no habla desde loopback — es un contenedor propio en la red
/// <c>caemanager-edge</c> (ver <c>docker-compose.produccion.yml</c>) — y la
/// aplicación creería que la petición es HTTP: generaría <c>Location: http://</c>
/// en los redirects y eso rompe el login vía <c>form-action</c> de la CSP.
/// </para>
///
/// <para>
/// <b>De qué depende entonces que la IP del cliente sea auténtica</b> (REC-019,
/// medido el 2026-09-18 contra <c>caddy:2.11.4-alpine</c>, el mismo digest que
/// fija el compose de producción). De dos supuestos, ninguno de ellos garantizado
/// por este código:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Caddy descarta el <c>X-Forwarded-For</c> entrante.</b> Comprobado en
///     un experimento real: con el Caddyfile de producción —que no configura
///     <c>trusted_proxies</c>— una petición con <c>X-Forwarded-For: 1.2.3.4</c>
///     llega al backend con la IP real del cliente y sin rastro del valor
///     falsificado; lo mismo con una cadena de varios saltos y con la cabecera
///     repetida. La documentación de Caddy lo declara — «by default, no proxies
///     are trusted» — y la mutación <c>trusted_proxies static 0.0.0.0/0</c>
///     hace pasar el valor falso, que es lo que demuestra que la propiedad la
///     gobierna esa opción y no otra cosa.
///   </description></item>
///   <item><description>
///     <b>Caddy es el único camino hasta el puerto 8080.</b> La aplicación no
///     publica <c>ports</c> en el compose de producción.
///   </description></item>
/// </list>
///
/// <para>
/// Los dos supuestos los vigila
/// <c>SupuestosDelProxyDeBordeTests</c> (Architecture.Tests): si alguien añade
/// <c>trusted_proxies</c> al Caddyfile o publica el puerto de la aplicación, el
/// trinquete se pone en rojo en vez de degradar en silencio.
/// </para>
///
/// <para>
/// <b>Hueco conocido.</b> La aplicación de staging comparte la red
/// <c>caemanager-edge</c> y puede alcanzar <c>caemanager-app:8080</c>
/// directamente, saltándose Caddy: desde ahí sí se puede imponer cualquier
/// <c>X-Forwarded-For</c>. El alcance de ese hueco es el limitador de tasa del
/// login (la única cosa del sistema que lee la IP del cliente — no hay ninguna
/// columna de IP en auditoría, ni en los registros de acceso sensible, ni en
/// los logs), y presupone comprometer staging antes. Registrado en REC-019; no
/// se cierra aquí porque la solución toca la topología de red de producción.
/// </para>
/// </summary>
public static class CabecerasDeProxyDeBorde
{
    /// <summary>
    /// Construye las opciones con las que <c>Program.cs</c> llama a
    /// <c>UseForwardedHeaders</c>. Función pura para que el test mida la regla
    /// misma y no una copia suya escrita a mano.
    /// </summary>
    public static ForwardedHeadersOptions Opciones()
    {
        var opciones = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };

        opciones.KnownProxies.Clear();
        opciones.KnownIPNetworks.Clear();

        return opciones;
    }
}
