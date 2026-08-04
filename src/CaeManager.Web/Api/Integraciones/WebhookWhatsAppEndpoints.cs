using System.Security.Cryptography;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Integraciones;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Api.Integraciones;

/// <summary>
/// Webhook de WhatsApp Cloud API (Meta). A diferencia de Microsoft 365 (una
/// URL por conexión), Meta configura UNA sola URL de callback a nivel de
/// app para todas las líneas — por eso la ruta no lleva conexionId: la
/// línea receptora se identifica por el phone_number_id del payload.
///
/// Orden innegociable del POST (docs/MULTITENANCY.md § 8: el secreto se
/// verifica ANTES de confiar en el tenant que implica): 1) firma
/// X-Hub-Signature-256 (HMAC-SHA256 del cuerpo crudo con el App Secret,
/// comparación en tiempo constante — más fuerte que el clientState de
/// Graph, aquí Meta sí firma), 2) resolver tenant por phone_number_id,
/// 3) persistir EventoWebhook y responder — Meta exige 200 en menos de 3 s
/// y reintenta agresivamente, así que aquí nunca se procesa nada ni se
/// llama a Meta (ARQUITECTURA-INTEGRACIONES.md § 6.4); el trabajo real lo
/// hace IngestaWebhookWhatsAppHostedService, despertado por la señal.
/// </summary>
public static class WebhookWhatsAppEndpoints
{
    public static IEndpointRouteBuilder MapWebhookWhatsAppEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var grupo = endpoints.MapGroup("/api/integraciones/webhooks/whatsapp").AllowAnonymous();

        // Handshake de verificación del callback (Meta App Dashboard):
        // hub.mode=subscribe + hub.verify_token correcto → eco del challenge.
        grupo.MapGet("/", (
            [FromQuery(Name = "hub.mode")] string? modo,
            [FromQuery(Name = "hub.verify_token")] string? verifyToken,
            [FromQuery(Name = "hub.challenge")] string? challenge,
            IOptions<WhatsAppCloudApiOptions> opciones) =>
        {
            var esperado = opciones.Value.VerifyToken;
            if (modo != "subscribe" || string.IsNullOrWhiteSpace(esperado) || string.IsNullOrWhiteSpace(verifyToken) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(verifyToken), Encoding.UTF8.GetBytes(esperado)))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            return Results.Text(challenge ?? string.Empty, "text/plain");
        });

        grupo.MapPost("/", async (
            HttpRequest request,
            IOptions<WhatsAppCloudApiOptions> opciones,
            IWhatsAppCloudApiClient whatsAppClient,
            IWebhookWhatsAppTenantResolver tenantResolver,
            IEventoWebhookRepository eventoRepositorio,
            IUnitOfWork unitOfWork,
            ISenalIngestaWhatsApp senal,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            using var lector = new StreamReader(request.Body);
            var payload = await lector.ReadToEndAsync(cancellationToken);

            var firma = request.Headers["X-Hub-Signature-256"].FirstOrDefault();
            if (!FirmaValida(payload, firma, opciones.Value.AppSecret))
            {
                logger.LogWarning("Notificación de webhook de WhatsApp rechazada: firma X-Hub-Signature-256 inválida o ausente.");
                return Results.Unauthorized();
            }

            var hayPendientes = false;
            foreach (var phoneNumberId in whatsAppClient.ExtraerPhoneNumberIds(payload))
            {
                var verificacion = await tenantResolver.ResolverPorPhoneNumberIdAsync(phoneNumberId, cancellationToken);
                if (!verificacion.Verificado)
                {
                    // Línea desconocida (p. ej. dada de baja): se ignora sin
                    // devolver error — un 4xx haría que Meta reintentara para
                    // siempre un payload que nunca vamos a poder atender.
                    logger.LogWarning("Webhook de WhatsApp para un phone_number_id desconocido: {PhoneNumberId}.", phoneNumberId);
                    continue;
                }

                using var _ = AmbitoTenantExplicito.Establecer(verificacion.TenantId!.Value);
                eventoRepositorio.Agregar(new EventoWebhook(verificacion.ConexionIntegracionId!.Value, payload));
                await unitOfWork.SaveChangesAsync(cancellationToken);
                hayPendientes = true;
            }

            if (hayPendientes)
                senal.Despertar();

            return Results.Ok();
        });

        return endpoints;
    }

    /// <summary>Pública solo para poder testearla directamente (función pura, sin InternalsVisibleTo en CaeManager.Web).</summary>
    public static bool FirmaValida(string payload, string? cabecera, string? appSecret)
    {
        const string prefijo = "sha256=";
        if (string.IsNullOrWhiteSpace(cabecera) || string.IsNullOrWhiteSpace(appSecret) ||
            !cabecera.StartsWith(prefijo, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] recibida;
        try
        {
            recibida = Convert.FromHexString(cabecera[prefijo.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        var esperada = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.UTF8.GetBytes(payload));
        return CryptographicOperations.FixedTimeEquals(esperada, recibida);
    }
}
