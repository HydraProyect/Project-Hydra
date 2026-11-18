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
/// Asignación de Operación que ya no autoriza, y a quien trabaja con el papel de Tenant de origen en un Tenant cuyo
/// Producto Contratado es solo Inbound (decisión del 2026-11-18, «Inbound sin Outbound»): el mismo criterio que
/// <see cref="GuardaPorServicioBehavior{TRequest,TResponse}"/> aplica a los comandos y <c>AlcanceDatosService</c> al
/// alcance (<see cref="ICriterioDeOutbound"/>). Sin esto, un Gestor CAE con papel Inbound, o la Gestora CAE de un Tenant
/// TCE sin Outbound, entraría en las páginas de Outbound que se autorizan solo por rol.
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
/// usuario y de su selección, que se resuelven en cada petición HTTP: el Router (<c>Routes</c> sin modo interactivo)
/// evalúa la página en cada petición, también en la navegación mejorada, no dentro de un circuito.
/// </para>
/// </summary>
public sealed class PaginaDeOutboundPorPapelAuthorizationHandler(
    ICurrentUserService currentUserService, ICriterioDeOutbound criterioDeOutbound) : IAuthorizationHandler
{
    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        if (!EsPaginaDeOutbound(context.Resource))
            return;

        if (!await criterioDeOutbound.AdmiteOutboundAsync(await currentUserService.ObtenerPapelActivoAsync()))
            context.Fail();
    }

    public static bool EsPaginaDeOutbound(Type? pagina) =>
        pagina is not null && pagina.IsDefined(typeof(PaginaDeOutboundAttribute), inherit: false);

    private static bool EsPaginaDeOutbound(object? recurso) => recurso switch
    {
        Microsoft.AspNetCore.Components.RouteData ruta => EsPaginaDeOutbound(ruta.PageType),
        HttpContext http when http.GetEndpoint() is { } endpoint =>
            endpoint.Metadata.GetMetadata<EndpointDeOutbound>() is not null
            || EsPaginaDeOutbound(endpoint.Metadata.GetMetadata<ComponentTypeMetadata>()?.Type),
        _ => false,
    };
}

/// <summary>
/// El criterio de Outbound (<see cref="ICriterioDeOutbound"/>) para un componente que filtra él mismo lo de Outbound (el
/// asistente del layout, los paneles de Outbound del hub de Configuración): no son la autoridad, que sigue en las páginas,
/// la guarda de comandos y el alcance. En una composición sin el criterio registrado (pruebas de componentes hechas a mano)
/// decide solo el papel, como antes de «Inbound sin Outbound»; la aplicación lo registra siempre (<c>AddApplication</c>).
/// </summary>
public static class CriterioDeOutboundEnComponentes
{
    public static async Task<bool> AdmiteOutboundAsync(IServiceProvider servicios, ICurrentUserService usuarioActual)
    {
        var papel = await usuarioActual.ObtenerPapelActivoAsync();
        return servicios.GetService(typeof(ICriterioDeOutbound)) is ICriterioDeOutbound criterio
            ? await criterio.AdmiteOutboundAsync(papel)
            : CriterioDePapel.AdmiteLoQueNoEsInbound(papel);
    }

    /// <summary>
    /// Para el menú lateral y la paleta: ¿cierra Outbound el Producto Contratado (papel de Tenant de origen y Tenant solo
    /// Inbound)? Un fallo al leerlo responde «no»: el menú no es autoridad, y ocultar Outbound por un error transitorio
    /// dejaría sin menú a un Tenant que lo usa; las páginas y los comandos siguen cerrados por su cuenta.
    /// </summary>
    public static async Task<bool> CierraPorSoloInboundAsync(IServiceProvider servicios, CancellationToken cancellationToken = default)
    {
        if (servicios.GetService(typeof(ICriterioDeOutbound)) is not ICriterioDeOutbound criterio
            || servicios.GetService(typeof(ICurrentUserService)) is not ICurrentUserService usuarioActual)
            return false;

        try
        {
            return await criterio.CierraPorSoloInboundAsync(await usuarioActual.ObtenerPapelActivoAsync(), cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}

/// <summary>
/// Marca de un endpoint mínimo de Outbound (la exportación o el archivo de una página de Outbound): quien exporta un
/// listado es quien lo ve, así que se niega con el mismo papel que su página. Un endpoint no tiene tipo que marcar, así
/// que lleva la marca en sus metadatos (patrón de <see cref="EndpointExcluidoDelEncargo"/>).
///
/// <para>
/// No cubre un panel embebido con <c>DynamicComponent</c> (ahí no hay evaluación de autorización de la página): el hub
/// de Configuración filtra él mismo sus paneles de Outbound por papel.
/// </para>
/// </summary>
public sealed class EndpointDeOutbound
{
    public static readonly EndpointDeOutbound Instancia = new();

    private EndpointDeOutbound() { }
}

public static class EndpointDeOutboundExtensions
{
    /// <summary>
    /// El endpoint es de Outbound: con papel Inbound, o con el de Tenant de origen en un Tenant solo Inbound, recibe la
    /// denegación estándar aunque el rol cumpla (<see cref="ICriterioDeOutbound"/>).
    /// </summary>
    public static TBuilder DeOutbound<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(EndpointDeOutbound.Instancia);
}
