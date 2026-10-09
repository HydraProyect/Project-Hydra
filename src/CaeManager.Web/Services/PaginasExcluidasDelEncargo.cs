using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Endpoints;

namespace CaeManager.Web.Services;

/// <summary>
/// Lista cerrada de las páginas que el Encargo de administración <b>no</b> abre
/// (decisión D-8, 2026-10-08), aunque el rol efectivo de quien administra por
/// encargo sea Administrador o Dirección CAE. Es la cara de pantalla de
/// <c>ActosExcluidosDelEncargo</c>, que es quien cierra las peticiones en
/// Application; esta lista evita que la página llegue a abrirse.
///
/// <para>
/// Toda página que pida Administrador o Dirección CAE sin admitir Coordinador
/// CAE tiene que estar aquí o declararse permitida en
/// <c>PaginasClasificadasParaElEncargoTests</c>: una pantalla nueva de
/// Administrador no se abre bajo encargo sin que alguien lo haya decidido.
/// </para>
/// </summary>
public static class PaginasExcluidasDelEncargo
{
    private static readonly HashSet<Type> Paginas =
    [
        typeof(Features.ApiKeys.Pages.ClavesApi),
        typeof(Features.Auditoria.Pages.Auditoria),
        typeof(Features.Auditoria.Pages.AccesosDocumentosSensibles),
        typeof(Features.AuditoriaIa.Pages.AuditoriaIa),
        typeof(Features.Clientes.Pages.ImportarClientes),
        typeof(Features.Clientes.Pages.ImportarCombinado),
        typeof(Features.Configuracion.Pages.SeleccionarClienteLecturaIa),
        typeof(Features.Documentos.Pages.ImportarDocumentos),
        typeof(Features.Facturacion.Pages.Facturacion),
        typeof(Features.GestionRoles.Pages.Roles),
        typeof(Features.Importacion.Pages.Importacion),
        typeof(Features.Integraciones.Pages.Conexiones),
        typeof(Features.Retencion.Pages.Retencion),
    ];

    public static bool Contiene(Type? pagina) => pagina is not null && Paginas.Contains(pagina);

    /// <summary>El principal lleva el claim que marca que su rol está elevado por un encargo.</summary>
    public static bool ActuaPorEncargo(System.Security.Claims.ClaimsPrincipal usuario) =>
        usuario.HasClaim(c => c.Type == RolEfectivoDelWorkspaceMiddleware.TipoClaimEncargoAdministracion);
}

/// <summary>
/// Marca de un endpoint mínimo que el Encargo de administración no abre. Las
/// páginas se nombran en <see cref="PaginasExcluidasDelEncargo"/>; un endpoint
/// no tiene tipo que nombrar, así que lleva la marca en sus metadatos.
/// </summary>
public sealed class EndpointExcluidoDelEncargo
{
    public static readonly EndpointExcluidoDelEncargo Instancia = new();

    private EndpointExcluidoDelEncargo() { }
}

public static class EndpointExcluidoDelEncargoExtensions
{
    /// <summary>
    /// El endpoint queda fuera del Encargo de administración: quien administra
    /// por encargo recibe 403 aunque su rol elevado cumpla el
    /// <c>RequireRole</c>. Obligatorio en todo endpoint que pida Administrador o
    /// Dirección CAE sin admitir Coordinador CAE
    /// (<c>PaginasClasificadasParaElEncargoTests</c>).
    /// </summary>
    public static TBuilder ExcluidoDelEncargoDeAdministracion<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(EndpointExcluidoDelEncargo.Instancia);
}

/// <summary>
/// Niega las páginas y los endpoints excluidos a quien administra por Encargo
/// de administración. No está ligado a ningún requisito: ASP.NET lo invoca en
/// <b>cada</b> evaluación de autorización, y un <c>Fail()</c> explícito gana a
/// cualquier requisito cumplido, incluido el <c>Roles = Administrador</c> que
/// el rol elevado satisface.
///
/// <para>
/// Solo restringe, y solo a quien lleva el claim del encargo: para cualquier
/// otro principal no hace nada. El recurso es la página
/// (<see cref="Microsoft.AspNetCore.Components.RouteData"/>, lo que evalúa
/// <c>AuthorizeRouteView</c> dentro del circuito) o la petición HTTP
/// (<see cref="HttpContext"/>, lo que evalúa el middleware de autorización para
/// el primer render de la página y para los endpoints mínimos).
/// </para>
///
/// <para>
/// No cubre un panel que el hub de Configuración embebe con
/// <c>DynamicComponent</c>: ahí no hay ruta ni evaluación de autorización de la
/// página embebida. Esa puerta la cierra el propio hub, que no ofrece ni
/// resuelve las entradas de <see cref="PaginasExcluidasDelEncargo"/>.
/// </para>
/// </summary>
public sealed class PaginasExcluidasDelEncargoAuthorizationHandler : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        if (PaginasExcluidasDelEncargo.ActuaPorEncargo(context.User) && EstaExcluido(context.Resource))
            context.Fail();

        return Task.CompletedTask;
    }

    private static bool EstaExcluido(object? recurso) => recurso switch
    {
        Microsoft.AspNetCore.Components.RouteData ruta => PaginasExcluidasDelEncargo.Contiene(ruta.PageType),
        HttpContext http when http.GetEndpoint() is { } endpoint =>
            endpoint.Metadata.GetMetadata<EndpointExcluidoDelEncargo>() is not null
            || PaginasExcluidasDelEncargo.Contiene(endpoint.Metadata.GetMetadata<ComponentTypeMetadata>()?.Type),
        _ => false,
    };
}
