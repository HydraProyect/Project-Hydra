using CaeManager.Application.Common;
using Microsoft.AspNetCore.Http;

namespace CaeManager.Infrastructure.Autenticacion;

/// <summary>
/// Declara, mientras corre el handler del endpoint, que quien actúa es una <b>integración
/// externa</b> (P41c): un sistema de un tercero que llama a TALVEG, no una
/// persona. Se aplica al <i>grupo</i> de endpoints y no a cada handler, para
/// que un endpoint nuevo del grupo no pueda olvidarlo.
///
/// <para>
/// Cubre dos clases de llamador, que llegan por caminos de autenticación
/// distintos y por eso no se podían resolver en el handler de la clave:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Los webhooks anónimos</b> de proveedor —Stripe, Microsoft 365,
/// WhatsApp—. Son <c>AllowAnonymous</c>: no hay identidad de sesión, así que
/// sin este filtro cada escritura de su endpoint se auditaba como
/// <c>Desconocido</c>. El proveedor se acredita por firma o
/// <c>clientState</c>, no por sesión.
/// </item>
/// <item>
/// <b>El grupo <c>/api/v1</c></b>, autenticado con <c>ClaveApi</c>. Hoy solo
/// mapea <c>GET</c> y sus consultas no escriben, así que no hay ninguna fila
/// que corregir; el filtro queda para que la primera escritura futura de ese
/// grupo no pase por una persona (el handler mete el Id de la clave como
/// <c>NameIdentifier</c>, así que sin él el tipo de actor se resolvería como
/// <c>Persona</c>).
/// </item>
/// </list>
///
/// <para>
/// <b>Lo que NO cubre.</b> La autenticación ocurre antes de que corra ningún
/// filtro de endpoint, así que la escritura del propio handler de clave
/// (<c>ClaveApi.RegistrarUso</c>) declara su propio ámbito: ver
/// <see cref="ApiKeyAuthenticationHandler"/>. Tampoco cubre los flujos
/// anónimos de <b>personas</b> —login, doble factor, restablecer contraseña,
/// callback SSO—: ahí actúa un humano todavía sin identificar, no una
/// integración, y etiquetarlo así sería falso. Esos siguen en
/// <c>Desconocido</c> hasta que el propietario decida qué valor les corresponde.
/// </para>
///
/// <para>
/// <b>Tampoco cubre la ejecución del resultado.</b> En las API mínimas el filtro
/// envuelve la invocación del handler; el <c>IResult</c> que este devuelve se
/// ejecuta después, con el ámbito ya liberado (medido en
/// <c>FiltroDeActorYEjecucionDelResultadoTests</c>). Una escritura auditable hecha
/// dentro de <c>ExecuteAsync</c> de un resultado saldría como <c>Desconocido</c>.
/// Hoy no ocurre porque ningún endpoint de estos grupos devuelve un resultado que
/// escriba —lo sostiene <c>SuperficiesAnonimasClasificadasPorActorTests</c>—; si
/// alguna vez hiciera falta, el ámbito tendría que declararse en un middleware que
/// envuelva el endpoint entero.
/// </para>
///
/// <para>
/// El ámbito es un <see cref="AsyncLocal{T}"/>. El <c>using</c> lo libera al
/// terminar, también si el endpoint lanza; el aislamiento entre peticiones no
/// depende de eso —el contexto de ejecución de cada petición ya es propio—, así
/// que el <c>using</c> es higiene, no la garantía, y ningún test puede distinguirlo
/// desde fuera de un método <c>async</c>.
/// </para>
/// </summary>
public sealed class ActorIntegracionExternaEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        using var ambitoActor = AmbitoActorAuditoria.EstablecerIntegracionExterna();

        return await next(context);
    }
}
