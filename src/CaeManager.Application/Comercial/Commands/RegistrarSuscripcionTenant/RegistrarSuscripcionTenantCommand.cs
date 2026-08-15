using CaeManager.Application.Comercial.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
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
/// </summary>
public record RegistrarSuscripcionTenantCommand(Guid TenantId, string StripeSubscriptionId) : ICommand;

public class RegistrarSuscripcionTenantCommandValidator : AbstractValidator<RegistrarSuscripcionTenantCommand>
{
    public RegistrarSuscripcionTenantCommandValidator()
    {
        RuleFor(c => c.TenantId).NotEmpty().WithMessage("Indica el tenant al que vincular la suscripción.");

        RuleFor(c => c.StripeSubscriptionId)
            .NotEmpty().WithMessage("Indica el Id de la suscripción de Stripe (empieza por \"sub_\").")
            .MaximumLength(100);
    }
}

public class RegistrarSuscripcionTenantCommandHandler(
    ITenantsQueryContext dbContext,
    IPaymentProvider paymentProvider,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<RegistrarSuscripcionTenantCommand, Result>
{
    public async Task<Result> Handle(RegistrarSuscripcionTenantCommand request, CancellationToken cancellationToken)
    {
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        var esPlataforma = tenantOrigenId is not null && await dbContext.Tenants
            .AnyAsync(t => t.Id == tenantOrigenId.Value && t.EsPlataforma, cancellationToken);

        if (!esPlataforma)
            return Result.Fallo(Error.Crear(
                "Comercial.NoAutorizado", "Solo el administrador de la plataforma puede gestionar suscripciones."));

        var tenant = await dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken);
        if (tenant is null)
            return Result.Fallo(Error.Crear("Comercial.TenantNoEncontrado", "No encontramos ese tenant."));

        if (tenant.EsPlataforma)
            return Result.Fallo(Error.Crear(
                "Comercial.TenantPlataforma", "El tenant de plataforma no es un tenant suscriptor."));

        var suscripcion = await paymentProvider.ObtenerSuscripcionAsync(request.StripeSubscriptionId, cancellationToken);
        if (suscripcion.EsFallido)
            return Result.Fallo(suscripcion.Error);

        var estadoInicial = MapeoEstadoComercialTenant.Desde(suscripcion.Valor.Estado);
        if (estadoInicial is null)
            return Result.Fallo(Error.Crear(
                "Comercial.EstadoNoReconocido", "Stripe devolvió un estado de suscripción que no reconocemos. Revisa la suscripción directamente en Stripe."));

        tenant.VincularSuscripcionStripe(suscripcion.Valor.IdCliente, suscripcion.Valor.IdSuscripcion, estadoInicial.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
