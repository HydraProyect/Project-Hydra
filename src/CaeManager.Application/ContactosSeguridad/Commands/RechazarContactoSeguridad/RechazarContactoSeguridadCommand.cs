using CaeManager.Application.Common;
using CaeManager.Application.ContactosSeguridad.Commands.ValidarContactoSeguridad;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.ContactosSeguridad.Commands.RechazarContactoSeguridad;

/// <summary>
/// Un Actor de Plataforma TALVEG rechaza, con motivo, el contacto de seguridad propuesto por el
/// Administrador de un Tenant beneficiario. El contacto vigente, si lo hay, no cambia. Misma autoridad y
/// misma puerta de escritura que <see cref="ValidarContactoSeguridadCommand"/>; el motivo lo ve el
/// Administrador del Tenant en su pantalla y queda en la auditoría de su Tenant.
/// </summary>
public record RechazarContactoSeguridadCommand(Guid ContactoId, string Motivo) : ICommand;

public class RechazarContactoSeguridadCommandValidator : AbstractValidator<RechazarContactoSeguridadCommand>
{
    public RechazarContactoSeguridadCommandValidator()
    {
        RuleFor(c => c.ContactoId).NotEmpty().WithMessage("Indica la propuesta que quieres rechazar.");
        RuleFor(c => c.Motivo)
            .Must(m => !string.IsNullOrWhiteSpace(m)).WithMessage("Indica el motivo del rechazo.")
            .MaximumLength(ContactoSeguridadTenant.LongitudMaximaMotivoRechazo)
            .WithMessage($"El motivo no puede superar {ContactoSeguridadTenant.LongitudMaximaMotivoRechazo} caracteres.");
    }
}

public class RechazarContactoSeguridadCommandHandler(
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    IAutorizacionAdminPlataforma autorizacion,
    IContactosSeguridadPlataforma plataforma)
    : IRequestHandler<RechazarContactoSeguridadCommand, Result>
{
    public async Task<Result> Handle(RechazarContactoSeguridadCommand request, CancellationToken cancellationToken)
    {
        if (!await AutorizacionResolucionContactoSeguridad.PuedeAsync(
                currentUserService, sesionPrivilegiadaActual, autorizacion, cancellationToken))
            return Result.Fallo(ErroresContactoSeguridad.SoloActorDePlataforma);

        if (string.IsNullOrWhiteSpace(request.Motivo))
            return Result.Fallo(ErroresContactoSeguridad.MotivoObligatorio);

        if (await AutorizacionResolucionContactoSeguridad.LaPropusoQuienResuelveAsync(
                currentUserService, plataforma, request.ContactoId, cancellationToken))
            return Result.Fallo(ErroresContactoSeguridad.MismaCuentaProponente);

        return await plataforma.ResolverAsync(request.ContactoId, request.Motivo.Trim(), cancellationToken);
    }
}
