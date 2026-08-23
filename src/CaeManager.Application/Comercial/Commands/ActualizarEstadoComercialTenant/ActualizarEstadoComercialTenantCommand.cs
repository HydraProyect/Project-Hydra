using CaeManager.Application.Comercial.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comercial.Commands.ActualizarEstadoComercialTenant;

/// <summary>
/// Resincronización manual del estado comercial de un tenant contra Stripe —
/// botón "Actualizar desde Stripe" del panel de plataforma, para cuando el
/// operador quiere confirmar el estado sin esperar al próximo webhook (o
/// mientras no hay un endpoint de webhook configurado todavía en el
/// dashboard de Stripe, ver el informe de esta fase). Misma consulta a
/// Stripe y mismo mapeo que <c>RegistrarSuscripcionTenantCommand</c> — solo
/// cambia que aquí el tenant ya tiene una suscripción vinculada.
/// </summary>
public record ActualizarEstadoComercialTenantCommand(Guid TenantId) : ICommand;

public class ActualizarEstadoComercialTenantCommandValidator : AbstractValidator<ActualizarEstadoComercialTenantCommand>
{
    public ActualizarEstadoComercialTenantCommandValidator()
    {
        RuleFor(c => c.TenantId).NotEmpty();
    }
}

public class ActualizarEstadoComercialTenantCommandHandler(
    ITenantsQueryContext dbContext,
    IPaymentProvider paymentProvider,
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ActualizarEstadoComercialTenantCommand, Result>
{
    public async Task<Result> Handle(ActualizarEstadoComercialTenantCommand request, CancellationToken cancellationToken)
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
                "Comercial.NoAutorizado", "No tienes autorización de plataforma sobre ese cliente."));

        var tenant = await dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken);
        if (tenant is null)
            return Result.Fallo(Error.Crear("Comercial.TenantNoEncontrado", "No encontramos ese tenant."));

        if (tenant.StripeSubscriptionId is null)
            return Result.Fallo(Error.Crear(
                "Comercial.SinSuscripcionVinculada", "Este tenant todavía no tiene ninguna suscripción de Stripe vinculada."));

        var suscripcion = await paymentProvider.ObtenerSuscripcionAsync(tenant.StripeSubscriptionId, cancellationToken);
        if (suscripcion.EsFallido)
            return Result.Fallo(suscripcion.Error);

        var nuevoEstado = MapeoEstadoComercialTenant.Desde(suscripcion.Valor.Estado);
        if (nuevoEstado is null)
            return Result.Fallo(Error.Crear(
                "Comercial.EstadoNoReconocido", "Stripe devolvió un estado de suscripción que no reconocemos. Revisa la suscripción directamente en Stripe."));

        tenant.ActualizarEstadoComercial(nuevoEstado.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
