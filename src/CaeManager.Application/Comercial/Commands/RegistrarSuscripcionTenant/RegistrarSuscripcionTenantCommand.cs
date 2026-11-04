using CaeManager.Application.Comercial.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comercial.Commands.RegistrarSuscripcionTenant;

/// <summary>
/// Alta manual de la suscripción de un tenant (plan de negocio § 1.7): el
/// operador de plataforma crea el Customer y la Subscription en Stripe fuera
/// de Hydra (vía Payment Link — Hydra no construye checkout de autoservicio
/// en este Horizonte) y aquí solo registra el Id de esa Subscription. El
/// Id de Customer y el estado inicial se toman de Stripe mismo
/// (<see cref="IPaymentProvider.ObtenerSuscripcionAsync"/>), nunca del texto
/// que el operador tecleó — así un Id de Subscription copiado mal, o de otro
/// Customer, falla aquí en vez de vincularse en silencio a lo que sea.
///
/// Mismo criterio de autorización que <c>GenerarClaveApiCommand</c>: solo el
/// Administrador del tenant plataforma. La lista blanca de roles con
/// escritura la sigue aplicando <c>AutorizacionEscrituraBehavior</c> antes de
/// llegar aquí; esta comprobación adicional es la misma "solo desde el
/// tenant de plataforma" — sin ella, cualquier Administrador de cualquier
/// tenant cliente podría vincular una suscripción a otro tenant.
///
/// La lectura del Tenant, la consulta a Stripe y el guardado van en una transacción con
/// los candados de <see cref="IBloqueoSuscripcionStripe"/> (Tenant, y después la
/// Subscription nueva y la que sustituye), que comparte con el webhook y la
/// resincronización: un webhook que llegue a mitad del alta espera a que confirme y
/// encuentra el vínculo, en vez de responder «sin Tenant» y perderse. Bajo esos mismos
/// candados se rechaza una Subscription ya vinculada a otro Tenant: el webhook resuelve el
/// Tenant por ese Id, y dos Tenants con la misma Subscription harían su resultado
/// indeterminado.
/// </summary>
public record RegistrarSuscripcionTenantCommand(Guid TenantId, string StripeSubscriptionId) : ICommand;

public class RegistrarSuscripcionTenantCommandValidator : AbstractValidator<RegistrarSuscripcionTenantCommand>
{
    public RegistrarSuscripcionTenantCommandValidator()
    {
        RuleFor(c => c.TenantId).NotEmpty().WithMessage("Indica el tenant al que vincular la suscripción.");

        RuleFor(c => c.StripeSubscriptionId)
            .NotEmpty().WithMessage("Indica el Id de la suscripción de Stripe (empieza por \"sub_\").")
            .MaximumLength(100)
            .Must(id => id is not null && id.StartsWith("sub_", StringComparison.Ordinal))
                .WithMessage("El Id de la suscripción de Stripe debe empezar por \"sub_\".");
    }
}

public class RegistrarSuscripcionTenantCommandHandler(
    ITenantsQueryContext dbContext,
    IPaymentProvider paymentProvider,
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork,
    ITransaccionDeComando transaccion,
    IBloqueoSuscripcionStripe bloqueo,
    IDescarteCambiosPendientes descarte)
    : IRequestHandler<RegistrarSuscripcionTenantCommand, Result>
{
    public async Task<Result> Handle(RegistrarSuscripcionTenantCommand request, CancellationToken cancellationToken)
    {
        // La autoridad es la CONCESIÓN, no la pertenencia a un tenant. Y acotada
        // al tenant sobre el que se actúa: una AdminPlataforma sobre el cliente A
        // no autoriza a tocar la suscripción del cliente B.
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear(
                "Comercial.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (!await autorizacion.PuedeSobreTenantAsync(usuarioId.Value, request.TenantId, cancellationToken))
            return Result.Fallo(Error.Crear(
                "Comercial.NoAutorizado", "No tienes autorización de plataforma sobre ese tenant."));

        // La operación puede ejecutarse más de una vez (estrategia de reintentos de
        // ITransaccionDeComando): todo lo que decide se lee dentro.
        return await transaccion.EjecutarAsync(ct => VincularBajoCandadoAsync(usuarioId.Value, request, ct), cancellationToken);
    }

    private async Task<Result> VincularBajoCandadoAsync(Guid usuarioId, RegistrarSuscripcionTenantCommand request, CancellationToken ct)
    {
        await bloqueo.BloquearTenantAsync(request.TenantId, ct);

        // La concesión pudo revocarse mientras se esperaba el candado.
        if (!await autorizacion.PuedeSobreTenantAsync(usuarioId, request.TenantId, ct))
            return Result.Fallo(Error.Crear(
                "Comercial.NoAutorizado", "No tienes autorización de plataforma sobre ese tenant."));

        // Contexto limpio tras el candado: en Blazor Server el del circuito puede conservar el
        // Tenant con la Subscription que tenía al cargarlo, y se bloquearía la que no es.
        var tenant = await LeerTenantAsync(request.TenantId, ct);
        if (tenant is null)
            return Result.Fallo(Error.Crear("Comercial.TenantNoEncontrado", "No encontramos ese tenant."));

        if (tenant.EsPlataforma)
            return Result.Fallo(Error.Crear(
                "Comercial.TenantPlataforma", "El tenant de plataforma no es un tenant suscriptor."));

        var anteriorVinculada = tenant.StripeSubscriptionId;
        var afectadas = anteriorVinculada is { } anterior
            ? new[] { request.StripeSubscriptionId, anterior }
            : [request.StripeSubscriptionId];
        await bloqueo.BloquearSuscripcionesAsync(afectadas, ct);

        // Bajo el candado de la Subscription nueva: otra alta concurrente con el mismo Id
        // espera aquí y, al seguir, ve el vínculo ya confirmado.
        var vinculadaAOtro = await dbContext.Tenants.AnyAsync(
            t => t.Id != request.TenantId && t.StripeSubscriptionId == request.StripeSubscriptionId, ct);
        if (vinculadaAOtro)
            return Result.Fallo(Error.Crear(
                "Comercial.SuscripcionYaVinculada", "Esa suscripción de Stripe ya está vinculada a otro tenant."));

        // Releer bajo todos los candados: un webhook de la Subscription anterior pudo cambiar el
        // estado comercial entre la primera lectura y su candado, y EF no marcaría como modificado
        // un valor igual al original que tenía guardado. El vínculo no cambia: lo fija el candado del Tenant.
        tenant = await LeerTenantAsync(request.TenantId, ct);
        if (tenant is null || tenant.StripeSubscriptionId != anteriorVinculada)
            throw new InvalidOperationException("El vínculo del Tenant cambió bajo su candado.");

        var suscripcion = await paymentProvider.ObtenerSuscripcionAsync(request.StripeSubscriptionId, ct);
        if (suscripcion.EsFallido)
            return Result.Fallo(suscripcion.Error);

        var estadoInicial = MapeoEstadoComercialTenant.Desde(suscripcion.Valor.Estado);
        if (estadoInicial is null)
            return Result.Fallo(Error.Crear(
                "Comercial.EstadoNoReconocido", "Stripe devolvió un estado de suscripción que no reconocemos. Revisa la suscripción directamente en Stripe."));

        tenant.VincularSuscripcionStripe(suscripcion.Valor.IdCliente, suscripcion.Valor.IdSuscripcion, estadoInicial.Value);
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Exito();
    }

    private Task<Tenant?> LeerTenantAsync(Guid tenantId, CancellationToken ct)
    {
        descarte.DescartarCambiosPendientes();
        return dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, ct);
    }
}
