using CaeManager.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Endpoints;

namespace CaeManager.Web.Services.Autorizacion;

/// <summary>
/// Marca de una página de Outbound (B-02b, cierre de su hueco declarado): la abre el rol de la página y, además, un papel
/// que admita lo que no es Inbound (<see cref="CriterioDePapel"/>). La pone <c>@attribute [PaginaDeOutbound]</c> en toda
/// página que el registro de módulos de ADR-013 (M0a) asigna al módulo Outbound; <c>PaginasDeOutboundPorPapelTests</c>
/// lo exige, y también que ninguna página de otro módulo la lleve.
///
/// <para>
/// No es un <see cref="AuthorizeAttribute"/>: la página conserva su único <c>[Authorize]</c> (roles o política), que es
/// lo que dispara la evaluación de autorización, y esta marca solo añade la condición de papel en
/// <see cref="PaginaDeOutboundPorPapelAuthorizationHandler"/>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PaginaDeOutboundAttribute : Attribute
{
}

/// <summary>
/// Niega una página de Outbound (<see cref="PaginaDeOutboundAttribute"/>) a quien trabaja con papel Inbound o con una
/// Asignación de Operación que ya no autoriza: el mismo criterio que <see cref="GuardaPorServicioBehavior{TRequest,TResponse}"/>
/// aplica a los comandos y <c>AlcanceDatosService</c> al alcance (<see cref="CriterioDePapel"/>). Sin esto, un Gestor CAE
/// con papel Inbound entraría en las páginas de Outbound que se autorizan solo por rol.
///
/// <para>
/// Mismo patrón que <see cref="PaginasExcluidasDelEncargoAuthorizationHandler"/>: no está ligado a ningún requisito,
/// ASP.NET lo invoca en <b>cada</b> evaluación de autorización y un <c>Fail()</c> explícito gana a cualquier requisito
/// cumplido. El recurso es la página (<see cref="Microsoft.AspNetCore.Components.RouteData"/>, en
/// <c>AuthorizeRouteView</c>) o la petición HTTP (<see cref="HttpContext"/>, en el middleware de autorización para el
/// primer render). La denegación es la de cualquier otra página: la de <c>AuthorizeRouteView</c> y la del middleware.
/// </para>
///
/// <para>
/// El papel sale de la operación revalidada en la base (<see cref="ICurrentUserService.ObtenerPapelActivoAsync"/>),
/// nunca del request; solo se consulta para una página marcada. Con ámbito, no singleton, porque el papel depende del
/// usuario y de la selección del circuito.
/// </para>
/// </summary>
public sealed class PaginaDeOutboundPorPapelAuthorizationHandler(ICurrentUserService currentUserService) : IAuthorizationHandler
{
    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        if (!EsPaginaDeOutbound(context.Resource))
            return;

        if (!CriterioDePapel.AdmiteLoQueNoEsInbound(await currentUserService.ObtenerPapelActivoAsync()))
            context.Fail();
    }

    public static bool EsPaginaDeOutbound(Type? pagina) =>
        pagina is not null && pagina.IsDefined(typeof(PaginaDeOutboundAttribute), inherit: false);

    private static bool EsPaginaDeOutbound(object? recurso) => recurso switch
    {
        Microsoft.AspNetCore.Components.RouteData ruta => EsPaginaDeOutbound(ruta.PageType),
        HttpContext http when http.GetEndpoint() is { } endpoint =>
            EsPaginaDeOutbound(endpoint.Metadata.GetMetadata<ComponentTypeMetadata>()?.Type),
        _ => false,
    };
}
