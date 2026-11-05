using System.Text;
using CaeManager.Application.Comercial.Common;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Web.Api.Integraciones;

namespace CaeManager.Web.Api.Comercial;

/// <summary>
/// Webhook de Stripe (Horizonte 1.7, "Billing mínimo viable"). Sin
/// autenticación de ASP.NET Core Identity — Stripe no manda cookie ni
/// ApiKey; la confianza se resuelve por completo con la firma
/// <c>Stripe-Signature</c> (HMAC contra el payload crudo, ver
/// <c>StripePaymentProvider.VerificarYLeerWebhook</c>), verificada ANTES de
/// confiar en nada del contenido — mismo orden innegociable que los demás
/// webhooks de este proyecto (Project-Hydra-Negocio/tecnico/docs/MULTITENANCY.md § 8). El cuerpo se lee
/// con <see cref="LimiteCuerpoWebhook"/> (mismo mecanismo que M365 y
/// WhatsApp) para no vaciar un stream sin cota antes de esa verificación.
///
/// A diferencia de los webhooks de Microsoft 365/WhatsApp, aquí SÍ se
/// procesa de forma síncrona dentro del propio request en vez de encolar
/// para un HostedService: el trabajo real es una consulta HTTPS a Stripe,
/// acotada a <see cref="ProcesadorWebhookSuscripcion.TiempoMaximoConsulta"/>, y
/// una escritura de una sola fila (<c>Tenant.ActualizarEstadoComercial</c>), bajo
/// el candado de la Subscription — no un pipeline de IA ni de ingesta de
/// documentos. Si la consulta falla o se agota, o el candado lo tiene otro, se
/// responde 503 sin escribir nada y Stripe reintenta.
///
/// No resuelve tenant por URL (a diferencia del webhook de Graph, una URL
/// por conexión): identifica el tenant afectado buscando por
/// <c>Tenant.StripeSubscriptionId</c>, que es la única correlación que
/// Hydra guarda con Stripe — de ahí que <see cref="ITenantsQueryContext"/>
/// no necesite ningún ámbito de tenant explícito aquí (Tenant no pertenece a
/// ningún tenant, ver TenantConfiguration). El estado que se aplica no es la copia
/// de la Subscription que trae el evento sino el que Stripe reporta al procesarlo
/// (<see cref="ProcesadorWebhookSuscripcion"/>): Stripe no garantiza el orden de entrega.
///
/// Si esa correlación resolviera al tenant de plataforma —nunca debería,
/// pero es deriva de datos, no una invariante de base de datos—, el evento
/// se ignora sin mutar (<c>Tenant.EsSuscribible</c>, ver
/// <see cref="TenantComercialExtensions"/> sobre por qué la comprobación
/// vive allí y no aquí).
/// </summary>
public static class WebhookStripeEndpoints
{
    public static IEndpointRouteBuilder MapWebhookStripeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // P41c: el llamador es un sistema de un tercero, no una persona, y este
        // endpoint es anónimo —no hay identidad de sesión que lo delate—. El
        // filtro va en el GRUPO para que ningún endpoint nuevo lo olvide; sin
        // él, cada escritura de aquí se auditaba como `Desconocido`.
        var grupo = endpoints.MapGroup("/api/comercial/webhooks/stripe")
            .AllowAnonymous()
            .AddEndpointFilter<ActorIntegracionExternaEndpointFilter>();

        grupo.MapPost("/", async (
            HttpRequest request,
            IPaymentProvider paymentProvider,
            IProcesadorWebhookSuscripcion procesador,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            var cuerpo = await LimiteCuerpoWebhook.LeerAsync(request, cancellationToken);
            if (cuerpo is null)
            {
                logger.LogWarning(
                    "Webhook de Stripe rechazado: cuerpo mayor de {Maximo} bytes.", LimiteCuerpoWebhook.MaximoBytes);
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var payload = Encoding.UTF8.GetString(cuerpo);
            var firma = request.Headers["Stripe-Signature"].FirstOrDefault();

            var evento = paymentProvider.VerificarYLeerWebhook(payload, firma);
            if (evento.EsFallido)
            {
                logger.LogWarning("Webhook de Stripe rechazado: {Error}", evento.Error.Mensaje);
                return Results.Unauthorized();
            }

            var resultado = await procesador.ProcesarAsync(evento.Valor, cancellationToken);
            var idSuscripcion = evento.Valor.Suscripcion?.IdSuscripcion;

            switch (resultado.Resultado)
            {
                case ResultadoWebhookSuscripcion.SinTenantVinculado:
                    // Suscripción todavía sin vincular a ningún tenant: p. ej. el webhook de
                    // creación llegó antes de que el operador completara el alta manual
                    // (RegistrarSuscripcionTenantCommand consulta el estado actual a Stripe en
                    // ese momento, así que no se pierde nada). No es un error.
                    logger.LogInformation(
                        "Webhook de Stripe para una suscripción sin tenant vinculado todavía: {IdSuscripcion}.", idSuscripcion);
                    return Results.Ok();

                case ResultadoWebhookSuscripcion.TenantNoSuscribible:
                    // Solo puede ser deriva de datos (RegistrarSuscripcionTenantCommand impide
                    // vincular el tenant de plataforma). 200 para que Stripe no reintente algo que
                    // no se arregla reintentando, pero en Warning: merece revisión manual.
                    logger.LogWarning(
                        "Webhook de Stripe {TipoEvento} para una suscripción vinculada al tenant de plataforma: {IdSuscripcion}. Se ignora sin mutar el estado comercial.",
                        evento.Valor.TipoEvento, idSuscripcion);
                    return Results.Ok();

                case ResultadoWebhookSuscripcion.EstadoNoReconocido:
                    // 200 de todas formas: un estado que TALVEG no sabe mapear no se arregla con
                    // que Stripe reintente. Queda en el log para revisión manual (ver
                    // ActualizarEstadoComercialTenantCommand para resincronizar a mano).
                    logger.LogWarning(
                        "Webhook de Stripe {TipoEvento} con un estado de suscripción no reconocido para {IdSuscripcion}.",
                        evento.Valor.TipoEvento, idSuscripcion);
                    return Results.Ok();

                case ResultadoWebhookSuscripcion.ProveedorNoDisponible:
                    // No se pudo leer el estado actual en Stripe: sin él no se escribe nada (ver
                    // ProcesadorWebhookSuscripcion sobre por qué no vale la copia del evento).
                    // Un código no 2xx hace que Stripe reintente el evento más tarde.
                    logger.LogWarning(
                        "Webhook de Stripe {TipoEvento} para {IdSuscripcion}: no se pudo consultar el estado actual en Stripe ({CodigoError}); se pide reintento.",
                        evento.Valor.TipoEvento, idSuscripcion, resultado.CodigoError);
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

                case ResultadoWebhookSuscripcion.SuscripcionOcupada:
                    // Otra entrega de la misma Subscription, o un alta manual a medias, tiene el
                    // candado. Esperarlo ocuparía una conexión del pool; Stripe reintenta el evento
                    // y, cuando llegue, quien tenía el candado ya habrá confirmado.
                    logger.LogInformation(
                        "Webhook de Stripe {TipoEvento} para {IdSuscripcion}: la suscripción se está procesando; se pide reintento.",
                        evento.Valor.TipoEvento, idSuscripcion);
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

                default:
                    // SinSuscripcion (eventos de factura o de cliente, reconocidos con 200 para que
                    // Stripe no los reintente), SinCambios y Aplicado.
                    return Results.Ok();
            }
        });

        return endpoints;
    }
}
