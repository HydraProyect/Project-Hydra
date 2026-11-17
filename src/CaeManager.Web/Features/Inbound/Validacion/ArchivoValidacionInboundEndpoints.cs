using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerLecturaInbound;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace CaeManager.Web.Features.Inbound.Validacion;

/// <summary>
/// El archivo de un Documento original del Tenant de la Empresa contratista (Tenant TEC), pedido desde la pantalla de
/// validación del Gestor CAE del Tenant TCE (demo Inbound P6). Es una ruta aparte de <c>/documentos/{id}/archivo</c>: esa
/// sirve Documentos del propio Tenant y, con papel Inbound, sigue cerrada (B-02b, O-1 (b)); esta solo sirve lo que la
/// autorización de lectura del Tenant TEC concede, por <see cref="ObtenerArchivoParaValidarInboundQuery"/>.
///
/// <para>
/// Falla cerrado con 404 en todo lo que dependa del Documento, sin un byte del archivo ni cabecera de descarga (en la
/// aplicación, <c>UseStatusCodePagesWithReExecute</c> pinta la página genérica de «no encontrado», igual para toda negativa
/// y para un Id que no existe, como en <c>/documentos/{id}/archivo</c>): la validación apagada
/// (<see cref="OpcionesValidacionInbound"/>), sin <c>ValidarInbound</c> o <c>LeerInbound</c>, fuera de la Asignación de
/// Cartera, o un Documento no autorizado, revocado, sustituido, de un tipo de salud sin marca o de un contrato de otro
/// Tenant TCE: la respuesta no dice si existe. Cada apertura que da lectura deja su fila <c>Apertura</c> en
/// <c>AccesosLecturaInbound</c> del Tenant TEC, escrita por la propia función de la base, con los mismos datos que el
/// <c>Listado</c>. Cabeceras como la de Outbound: sin caché, <c>inline</c> con nombre, y el <c>nosniff</c> y el
/// <c>X-Frame-Options: DENY</c> de toda la aplicación (no se incrusta: la pantalla no tiene visor).
/// </para>
/// </summary>
public static class ArchivoValidacionInboundEndpoints
{
    public const string Plantilla = "/inbound/validacion/contratos/{contratoAccesoId:guid}/documentos/{documentoId:guid}/archivo";

    /// <summary>Los roles de la pantalla de validación (<c>ValidacionDocumento</c>); Application vuelve a decidir con el rol efectivo.</summary>
    public const string RolesQueValidan = $"{Roles.Administrador},{Roles.DireccionCae},{Roles.CoordinadorCae},{Roles.GestorCae}";

    /// <summary>
    /// La ruta concreta, a partir de <see cref="Plantilla"/>: un único literal de ruta en el fichero, el que cataloga
    /// <c>EndpointsQueSirvenBinariosEstanCatalogadosTests</c>.
    /// </summary>
    public static string Ruta(Guid contratoAccesoId, Guid documentoId) =>
        Plantilla
            .Replace("{contratoAccesoId:guid}", contratoAccesoId.ToString(), StringComparison.Ordinal)
            .Replace("{documentoId:guid}", documentoId.ToString(), StringComparison.Ordinal);

    public static IEndpointRouteBuilder MapArchivoValidacionInboundEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Solo la cookie de sesión, como /documentos/{id}/archivo: ni token de extensión ni clave de la API.
        endpoints.MapGet(Plantilla, ServirAsync)
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = IdentityConstants.ApplicationScheme,
                Roles = RolesQueValidan,
            });
        return endpoints;
    }

    /// <summary>Manejador con nombre, y no una lambda, para probarlo sin levantar el host (como <c>DocumentosEndpoints</c>).</summary>
    public static async Task<IResult> ServirAsync(
        Guid contratoAccesoId, Guid documentoId, HttpContext contexto, ISender mediator,
        IOptions<OpcionesValidacionInbound> opciones, ILoggerFactory fabricaRegistro, CancellationToken cancellationToken)
    {
        // Apagada, ni se lee: la apertura deja traza en el Tenant TEC, como el listado de la cola.
        if (!opciones.Value.Habilitado)
            return Results.NotFound();

        ContenidoArchivoInboundDto? archivo;
        try
        {
            archivo = await mediator.Send(new ObtenerArchivoParaValidarInboundQuery(contratoAccesoId, documentoId), cancellationToken);
        }
        catch (Exception ex) when (ErroresLecturaSensible.EsMapeable(ex))
        {
            // Sin contexto firmado (42501) o un fallo transitorio: 403 o 503 sin cuerpo, como Outbound. No depende del Documento.
            return ErroresLecturaSensible.Responder(contexto, ex, documentoId,
                fabricaRegistro.CreateLogger(typeof(ArchivoValidacionInboundEndpoints).FullName!));
        }

        if (archivo is null)
            return Results.NotFound();

        CabecerasArchivoSensible.ProhibirCache(contexto);

        // En línea y con nombre propio, como Outbound: el enlace «Descargar» lleva el atributo download y el navegador lo
        // aplica sobre inline. El nombre no lleva nada del Tenant TEC: ni la referencia del almacén ni el Trabajador o la Empresa a la que se refiere
        // el Documento.
        var disposicion = new ContentDispositionHeaderValue("inline");
        disposicion.SetHttpFileName("documento" + Extension(archivo.TipoMime));
        contexto.Response.Headers.ContentDisposition = disposicion.ToString();

        return Results.File(archivo.Contenido, archivo.TipoMime);
    }

    private static string Extension(string tipoMime) => tipoMime switch
    {
        "application/pdf" => ".pdf",
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        _ => string.Empty,
    };
}
