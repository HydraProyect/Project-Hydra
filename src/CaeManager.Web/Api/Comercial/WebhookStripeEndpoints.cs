using CaeManager.Application.Comercial.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Web.Api.Comercial;

/// <summary>
/// Webhook de Stripe (Horizonte 1.7, "Billing mínimo viable"). Sin
/// autenticación de ASP.NET Core Identity — Stripe no manda cookie ni
/// ApiKey; la confianza se resuelve por completo con la firma
/// <c>Stripe-Signature</c> (HMAC contra el payload crudo, ver
/// <c>StripePaymentProvider.VerificarYLeerWebhook</c>), verificada ANTES de
/// confiar en nada del contenido — mismo orden innegociable que los demás
/// webhooks de este proyecto (docs/MULTITENANCY.md § 8).
///
/// A diferencia de los webhooks de Microsoft 365/WhatsApp, aquí SÍ se
/// procesa de forma síncrona dentro del propio request en vez de encolar
/// para un HostedService: el trabajo real es una escritura de una sola fila
/// (<c>Tenant.ActualizarEstadoComercial</c>), no un pipeline de IA ni de
/// ingesta de documentos — no hay nada que ganar aplazándolo, y Stripe
/// tolera hasta varios segundos de respuesta.
///
/// No resuelve tenant por URL (a diferencia del webhook de Graph, una URL
/// por conexión): identifica el tenant afectado buscando por
/// <c>Tenant.StripeSubscriptionId</c>, que es la única correlación que
/// Hydra guarda con Stripe — de ahí que <see cref="ITenantsQueryContext"/>
/// no necesite ningún ámbito de tenant explícito aquí (Tenant no pertenece a
/// ningún tenant, ver TenantConfiguration).
/// </summary>
public static class WebhookStripeEndpoints
{
    public static IEndpointRouteBuilder MapWebhookStripeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var grupo = endpoints.MapGroup("/api/comercial/webhooks/stripe").AllowAnonymous();

        grupo.MapPost("/", async (
            HttpRequest request,
            IPaymentProvider paymentProvider,
            ITenantsQueryContext dbContext,
            IUnitOfWork unitOfWork,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            using var lector = new StreamReader(request.Body);
            var payload = await lector.ReadToEndAsync(cancellationToken);
            var firma = request.Headers["Stripe-Signature"].FirstOrDefault();

            var evento = paymentProvider.VerificarYLeerWebhook(payload, firma);
            if (evento.EsFallido)
            {
                logger.LogWarning("Webhook de Stripe rechazado: {Error}", evento.Error.Mensaje);
                return Results.Unauthorized();
            }

            // Eventos reconocidos pero sin Subscription asociada (p. ej. de
            // factura o de cliente) — ver StripePaymentProvider sobre por
            // qué basta con reaccionar a customer.subscription.*. Se
            // reconocen con 200 para que Stripe no los reintente.
            if (evento.Valor.Suscripcion is not { } suscripcion)
                return Results.Ok();

            var nuevoEstado = MapeoEstadoComercialTenant.Desde(suscripcion.Estado);
            if (nuevoEstado is null)
            {
                logger.LogWarning(
                    "Webhook de Stripe {TipoEvento} con un estado de suscripción no reconocido para {IdSuscripcion}.",
                    evento.Valor.TipoEvento, suscripcion.IdSuscripcion);
                // 200 de todas formas: un estado que Hydra no sabe mapear no
                // se va a arreglar solo con que Stripe reintente el mismo
                // evento — seguirá sin reconocerlo. Queda en el log para
                // revisión manual (ver ActualizarEstadoComercialTenantCommand
                // para resincronizar a mano una vez corregido el código).
                return Results.Ok();
            }

            var tenant = await dbContext.Tenants
                .SingleOrDefaultAsync(t => t.StripeSubscriptionId == suscripcion.IdSuscripcion, cancellationToken);

            if (tenant is null)
            {
                // Suscripción todavía sin vincular a ningún tenant — p. ej.
                // el webhook de creación llegó antes de que el operador
                // completara el alta manual (RegistrarSuscripcionTenantCommand
                // ya consulta el estado actual a Stripe en ese momento, así
                // que no se pierde nada). No es un error: se ignora.
                logger.LogInformation(
                    "Webhook de Stripe para una suscripción sin tenant vinculado todavía: {IdSuscripcion}.", suscripcion.IdSuscripcion);
                return Results.Ok();
            }

            tenant.ActualizarEstadoComercial(nuevoEstado.Value);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            return Results.Ok();
        });

        return endpoints;
    }
}
