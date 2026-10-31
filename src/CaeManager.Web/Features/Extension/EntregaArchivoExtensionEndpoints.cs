using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.ConfirmarSubidaDesdeExtension;
using CaeManager.Application.Documentos.Queries.PrepararEntregaArchivoExtension;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Services;
using MediatR;
using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Web.Features.Extension;

/// <summary>
/// Entrega del archivo de una Acreditación pendiente a la extensión de navegador, para que el Gestor CAE lo ponga en el
/// formulario de la plataforma CAE de terceros, con su propia sesión allí. Es la única vía por la que la extensión
/// recibe contenido: <c>/documentos/{id}/archivo</c> solo admite la sesión interactiva.
///
/// <para>
/// <c>POST /extension/acreditaciones/{id}/entrega</c> con <c>{ dominioPortal }</c> comprueba cartera, estado, kill switch
/// y portal (<see cref="PrepararEntregaArchivoExtensionQuery"/>) y responde <c>{ ticket, expiraEnUtc }</c>.
/// <c>GET /extension/acreditaciones/{id}/archivo</c> con la cabecera <c>X-Ticket-Entrega</c> repite las comprobaciones
/// con el host fijado en el ticket y sirve el archivo con el registro de acceso a documento sensible. Un ticket
/// inválido, caducado, de otro usuario o de otra Acreditación responde el mismo 404.
/// </para>
///
/// <para>
/// Las dos rutas exigen el token de extensión (<see cref="Policies.SoloExtension"/>): la cookie no autentica, así que no
/// hay antifalsificación que validar. La política no impide que una petición traiga además la cookie de selección: que
/// una Sesión Privilegiada no opere aquí lo sostienen el cliente (<c>credentials: "omit"</c>), el rol efectivo, que bajo
/// ella es nulo, y la función de registro de Soporte TALVEG, que rechaza este tipo de acceso. Ninguna de las dos rutas
/// anota nada en la Acreditación: eso sigue siendo «Lo he enviado».
/// </para>
/// </summary>
public static class EntregaArchivoExtensionEndpoints
{
    public const string CabeceraTicket = "X-Ticket-Entrega";

    /// <param name="DominioPortal">El host de la pestaña donde va el documento (p. ej. «app.dokify.net»).</param>
    public sealed record PrepararEntregaPeticion(string? DominioPortal);

    public static IEndpointRouteBuilder MapEntregaArchivoExtensionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/extension/acreditaciones/{id:guid}/entrega", PrepararAsync)
            .RequireAuthorization(Policies.SoloExtension)
            .RequireRateLimiting(LimitadorEntregaExtension.Politica);

        endpoints.MapGet("/extension/acreditaciones/{id:guid}/archivo", ServirAsync)
            .RequireAuthorization(Policies.SoloExtension)
            .RequireRateLimiting(LimitadorEntregaExtension.Politica);

        return endpoints;
    }

    public static async Task<IResult> PrepararAsync(
        Guid id, HttpRequest solicitud, IMediator mediator, ICurrentUserService usuarioActual,
        IDataProtectionProvider dataProtectionProvider, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
            return Results.NotFound();

        // Sin cuerpo o sin JSON llega igual a la consulta, que es quien dice «no declaraste el portal».
        var peticion = await LeerPeticionAsync(solicitud, cancellationToken);
        var resultado = await mediator.Send(new PrepararEntregaArchivoExtensionQuery(id, peticion?.DominioPortal), cancellationToken);
        if (resultado.EsFallido)
            return Fallo(resultado.Error);

        if (await usuarioActual.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return Results.NotFound();

        var ticket = TicketEntregaExtension.Proteger(
            dataProtectionProvider, usuarioId, id, resultado.Valor.DocumentoId, resultado.Valor.HostPortal);
        return Results.Ok(new { ticket, expiraEnUtc = DateTime.UtcNow.Add(TicketEntregaExtension.Vigencia) });
    }

    /// <summary>Método con nombre, y no una lambda, para probarlo sin levantar el host (como <see cref="DocumentosEndpoints.ServirArchivoAsync"/>).</summary>
    public static async Task<IResult> ServirAsync(
        Guid id, HttpContext contexto, IMediator mediator, ICurrentUserService usuarioActual,
        IDataProtectionProvider dataProtectionProvider, IFileStorageService almacenamiento,
        IRegistroAccesoDocumentoSensibleService registroAcceso, ILoggerFactory fabricaRegistro,
        CancellationToken cancellationToken)
    {
        var ticket = TicketEntregaExtension.Leer(dataProtectionProvider, contexto.Request.Headers[CabeceraTicket].ToString());
        if (ticket is null || ticket.AcreditacionId != id
            || await usuarioActual.ObtenerUsuarioActualIdAsync() != ticket.UsuarioId)
            return Results.NotFound();

        // El ticket no es una foto válida durante un minuto: el kill switch, la cartera o una Acreditación que dejó de
        // estar pendiente surten efecto también aquí. El 404 va sin cuerpo: un Id ajeno no se distingue de un ticket inválido.
        var resultado = await mediator.Send(new PrepararEntregaArchivoExtensionQuery(id, ticket.HostPortal), cancellationToken);
        if (resultado.EsFallido)
            return EstadoDe(resultado.Error) == StatusCodes.Status404NotFound ? Results.NotFound() : Fallo(resultado.Error);
        if (resultado.Valor.DocumentoId != ticket.DocumentoId)
            return Results.NotFound();

        // La misma lectura con alcance y el mismo registro que una apertura, con su propio tipo de acceso. Sin rangos ni
        // incrustación: lo recoge un fetch de la extensión, no un visor.
        var abierto = await DocumentosEndpoints.AbrirConRegistroAsync(
            ticket.DocumentoId, TipoAccesoDocumentoSensible.EntregaExtension, contexto, mediator, almacenamiento,
            registroAcceso, fabricaRegistro, cancellationToken);
        return abierto.Rechazo ?? Results.File(abierto.Flujo!, "application/pdf");
    }

    private static async Task<PrepararEntregaPeticion?> LeerPeticionAsync(HttpRequest solicitud, CancellationToken cancellationToken)
    {
        if (!solicitud.HasJsonContentType()) return null;

        try
        {
            return await solicitud.ReadFromJsonAsync<PrepararEntregaPeticion>(cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Mismo reparto que «Lo he enviado» (MarcarAcreditacionSubidaEndpoints): no existe y fuera de cartera son el mismo
    // 404; lo que existe pero no se puede entregar es un 409 con su detalle, que la extensión muestra tal cual.
    private static IResult Fallo(Error error) => Results.Problem(error.Mensaje, statusCode: EstadoDe(error));

    private static int EstadoDe(Error error) => error.Codigo switch
    {
        ConfirmarSubidaDesdeExtensionCommandHandler.CodigoConectorInactivo
            or ConfirmarSubidaDesdeExtensionCommandHandler.CodigoPortalAjeno => StatusCodes.Status409Conflict,
        ConfirmarSubidaDesdeExtensionCommandHandler.CodigoPortalNoDeclarado => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status404NotFound,
    };
}
