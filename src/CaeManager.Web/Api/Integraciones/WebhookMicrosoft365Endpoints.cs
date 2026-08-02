using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Integraciones;

namespace CaeManager.Web.Api.Integraciones;

/// <summary>
/// Webhook de notificaciones de Microsoft Graph (P3-33). Sin autenticación
/// de ASP.NET Core Identity — Graph no manda cookie ni ApiKey; la confianza
/// se resuelve por el Id de <see cref="ConexionIntegracion"/> en la propia
/// URL más el <c>clientState</c> que Graph devuelve sin cambios en cada
/// notificación (docs/MULTITENANCY.md § 8, tercer modo de resolución de
/// tenant — ver <see cref="IWebhookTenantResolver"/>).
///
/// GET atiende el handshake de validación que Graph hace una vez al crear
/// la suscripción (echo del <c>validationToken</c>, ver
/// <c>Microsoft365GraphClient.CrearSuscripcionAsync</c>). POST atiende las
/// notificaciones reales — solo persiste <see cref="EventoWebhook"/> y
/// responde, nunca llama a Graph de forma síncrona dentro del request
/// entrante (ARQUITECTURA-INTEGRACIONES.md § 6.4) —
/// <c>IngestaWebhookHostedService</c> (Infrastructure) hace el trabajo real
/// fuera de este ciclo de petición/respuesta.
/// </summary>
public static class WebhookMicrosoft365Endpoints
{
    public static IEndpointRouteBuilder MapWebhookMicrosoft365Endpoints(this IEndpointRouteBuilder endpoints)
    {
        var grupo = endpoints.MapGroup("/api/integraciones/webhooks/microsoft365").AllowAnonymous();

        grupo.MapGet("/{conexionId:guid}", (string? validationToken) =>
            string.IsNullOrWhiteSpace(validationToken)
                ? Results.BadRequest()
                : Results.Text(validationToken, "text/plain"));

        grupo.MapPost("/{conexionId:guid}", async (
            Guid conexionId, HttpRequest request,
            IMicrosoft365GraphClient graphClient, IWebhookTenantResolver tenantResolver,
            IEventoWebhookRepository eventoRepositorio, IUnitOfWork unitOfWork,
            ILogger<Program> logger, CancellationToken cancellationToken) =>
        {
            using var lector = new StreamReader(request.Body);
            var payload = await lector.ReadToEndAsync(cancellationToken);

            var clientStateRecibido = graphClient.ExtraerClientStateDeNotificacion(payload);
            if (string.IsNullOrWhiteSpace(clientStateRecibido))
                return Results.BadRequest();

            var verificacion = await tenantResolver.VerificarAsync(conexionId, clientStateRecibido, cancellationToken);
            if (!verificacion.Verificado)
            {
                logger.LogWarning(
                    "Notificación de webhook de Microsoft 365 rechazada para la conexión {ConexionId}: clientState no coincide.", conexionId);
                return Results.Unauthorized();
            }

            using var _ = AmbitoTenantExplicito.Establecer(verificacion.TenantId!.Value);
            eventoRepositorio.Agregar(new EventoWebhook(conexionId, payload));
            await unitOfWork.SaveChangesAsync(cancellationToken);

            return Results.Accepted();
        });

        return endpoints;
    }
}
