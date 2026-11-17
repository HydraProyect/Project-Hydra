using CaeManager.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Endpoints;

namespace CaeManager.Web.Services.Autorizacion;

/// <summary>
/// Una página que se abre con papel Inbound (decisión O-1 (b), 2026-11-17): todo lo demás del Núcleo y de Facturación
/// nace cerrado con ese papel (<see cref="NucleoCerradoConPapelInboundAuthorizationHandler"/>). La llevan solo las de la
/// tabla acordada con Inbound (la cuenta propia y lo que la sesión necesita), y <c>NucleoPorPapelInboundTests</c> lo
/// exige: quitarla de una de esas, o ponerla en otra, da rojo.
///
/// <para>
/// No es un <see cref="AuthorizeAttribute"/>: la página conserva su <c>[Authorize]</c> (roles o política), que decide
/// quién entra; esta marca solo la exime de la condición de papel.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AbiertaConPapelInboundAttribute : Attribute
{
}

/// <summary>
/// Cierra con papel Inbound, por defecto, toda página y todo endpoint mínimo del Núcleo y de Facturación (decisión O-1
/// (b)): solo se abre lo que lleva <see cref="AbiertaConPapelInboundAttribute"/> o <c>.AbiertoConPapelInbound()</c>. Así
/// una página o un endpoint nuevos nacen cerrados con ese papel en vez de abiertos por olvido. Se niega solo con
/// <see cref="PapelActivo.Inbound"/>; el Tenant de origen, el papel Outbound y la Sesión Privilegiada (sin papel) no
/// pierden nada.
///
/// <para>
/// Una Asignación de Operación que ya no autoriza (<see cref="PapelActivo.OperacionQueYaNoAutoriza"/>) no cierra el
/// Núcleo, a diferencia de la guarda de comandos y de las páginas de Outbound (<see cref="CriterioDePapel"/>): O-1 (b)
/// cierra con papel Inbound, y ese estado no es un papel, sino una selección caducada que retira en la misma petición
/// <see cref="RevalidacionClienteActivoMiddleware"/>. Ese middleware corre después de <c>UseAuthorization</c>
/// (<c>Program.cs</c>), así que negar aquí cortaba la petición hacia «Acceso denegado» antes de que la revalidación
/// devolviera la sesión al Tenant de origen (el E2E <c>PropuestaApoyoCarteraE2ETests</c>: tras desasignarse, Trabajadores
/// debe pintarse en el origen). Lo que impide operar o leer con esa selección no cambia: la guarda de comandos la
/// niega, <c>RolEfectivoDelWorkspaceMiddleware</c> retira el rol, el alcance de datos de Outbound queda vacío y, dentro
/// de un circuito ya vivo, la revalida <c>RevalidacionCircuitoActivoHandler</c>; es lo mismo que había antes de O-1 (b).
/// </para>
///
/// <para>
/// Qué se cierra. Una página (<see cref="Microsoft.AspNetCore.Components.RouteData"/> en <c>AuthorizeRouteView</c>, o el
/// <see cref="ComponentTypeMetadata"/> del endpoint en el middleware de autorización) salvo que sea de Inbound (su
/// espacio de nombres, el mismo criterio que el registro de módulos de ADR-013), que lleve la marca de Outbound (la
/// cierra ya <see cref="PaginaDeOutboundPorPapelAuthorizationHandler"/>, y así el papel se consulta una sola vez) o que
/// esté abierta. Un endpoint mínimo, si se mapea en el grupo del Núcleo y de Facturación de <c>Program.cs</c>
/// (<see cref="EndpointDelNucleoOFacturacionExtensions.DelNucleoOFacturacion{TBuilder}"/>) y no está abierto. Lo que no
/// pasa por una evaluación de autorización (las páginas y endpoints anónimos) no tiene sesión ni papel que mirar.
/// </para>
///
/// <para>
/// Mismo patrón que <see cref="PaginaDeOutboundPorPapelAuthorizationHandler"/>: no está ligado a ningún requisito, ASP.NET
/// lo invoca en cada evaluación y un <c>Fail()</c> gana a cualquier requisito cumplido. El papel sale de la operación
/// revalidada en la base (<see cref="ICurrentUserService.ObtenerPapelActivoAsync"/>, que consulta por la
/// <c>PuertaAccesoDatos</c>), solo para lo que se cierra; con ámbito porque depende de la sesión de cada petición.
/// </para>
/// </summary>
public sealed class NucleoCerradoConPapelInboundAuthorizationHandler(ICurrentUserService currentUserService) : IAuthorizationHandler
{
    /// <summary>Espacio de nombres de las pantallas Inbound (registro de módulos: <c>area Web.Features.Inbound</c>).</summary>
    public const string EspacioDeInbound = "CaeManager.Web.Features.Inbound";

    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        if (!SeCierraConPapelInbound(context.Resource))
            return;

        // Solo papel Inbound (O-1 (b)); una operación que ya no autoriza la retira la revalidación (ver el resumen).
        if (await currentUserService.ObtenerPapelActivoAsync() == PapelActivo.Inbound)
            context.Fail();
    }

    /// <summary>¿Cierra este handler la página con papel Inbound?</summary>
    public static bool SeCierraConPapelInbound(Type? pagina) =>
        pagina is not null
        && !pagina.IsDefined(typeof(AbiertaConPapelInboundAttribute), inherit: false)
        && !PaginaDeOutboundPorPapelAuthorizationHandler.EsPaginaDeOutbound(pagina)
        && !EsDeInbound(pagina);

    public static bool EsDeInbound(Type tipo) =>
        tipo.Namespace is { } espacio
        && (espacio == EspacioDeInbound || espacio.StartsWith(EspacioDeInbound + ".", StringComparison.Ordinal));

    private static bool SeCierraConPapelInbound(object? recurso) => recurso switch
    {
        Microsoft.AspNetCore.Components.RouteData ruta => SeCierraConPapelInbound(ruta.PageType),
        HttpContext http when http.GetEndpoint() is { } endpoint =>
            endpoint.Metadata.GetMetadata<ComponentTypeMetadata>() is { } pagina
                ? SeCierraConPapelInbound(pagina.Type)
                : endpoint.Metadata.GetMetadata<EndpointDelNucleoOFacturacion>() is not null
                  && endpoint.Metadata.GetMetadata<EndpointAbiertoConPapelInbound>() is null,
        _ => false,
    };
}

/// <summary>
/// Metadato del grupo de <c>Program.cs</c> en el que se mapean los endpoints mínimos del Núcleo y de Facturación: con
/// papel Inbound se cierran todos salvo los abiertos (<see cref="EndpointAbiertoConPapelInbound"/>). Un endpoint nuevo
/// en una clase ya mapeada en el grupo nace cerrado; una clase nueva del Núcleo o de Facturación fuera del grupo la
/// detecta <c>NucleoPorPapelInboundTests</c>.
/// </summary>
public sealed class EndpointDelNucleoOFacturacion
{
    public static readonly EndpointDelNucleoOFacturacion Instancia = new();

    private EndpointDelNucleoOFacturacion() { }
}

/// <summary>Un endpoint del grupo del Núcleo y de Facturación que se abre con papel Inbound (tabla de O-1 (b)).</summary>
public sealed class EndpointAbiertoConPapelInbound
{
    public static readonly EndpointAbiertoConPapelInbound Instancia = new();

    private EndpointAbiertoConPapelInbound() { }
}

public static class EndpointDelNucleoOFacturacionExtensions
{
    /// <summary>Los endpoints de este grupo son del Núcleo o de Facturación: con papel Inbound, cerrados por defecto.</summary>
    public static TBuilder DelNucleoOFacturacion<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(EndpointDelNucleoOFacturacion.Instancia);

    /// <summary>Este endpoint del Núcleo se abre con papel Inbound: la sesión o la cuenta propia lo necesitan.</summary>
    public static TBuilder AbiertoConPapelInbound<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(EndpointAbiertoConPapelInbound.Instancia);
}
