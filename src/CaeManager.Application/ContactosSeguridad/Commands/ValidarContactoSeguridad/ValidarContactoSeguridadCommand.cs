using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.ContactosSeguridad.Commands.ValidarContactoSeguridad;

/// <summary>
/// Un Actor de Plataforma TALVEG valida el contacto de seguridad que propuso el Administrador de un Tenant
/// beneficiario. Desde ese momento es el único que recibe los avisos de violación de seguridad de ese
/// Tenant, y el que estaba vigente queda Sustituido en la misma transacción.
///
/// <para>
/// <b>Autoridad.</b> La concesión AdminPlataforma <b>global</b> vigente, no el rol Administrador de
/// Identity ni pertenecer al Tenant de plataforma (mismo criterio que la cola de propuestas, que es
/// transversal: una concesión acotada no la lee). Fuera de una Sesión Privilegiada: es un acto del plano
/// de Plataforma desde el propio Tenant del actor, no una operación dentro del Tenant beneficiario.
/// </para>
///
/// <para>
/// <b>Cuatro ojos.</b> La cuenta que propuso el contacto no puede validarlo ni rechazarlo: el caso real es
/// un Actor de Plataforma TALVEG que, como Administrador del Tenant de plataforma, propone el contacto de
/// ese Tenant. Lo resuelve otro Actor de Plataforma TALVEG con la misma concesión.
/// </para>
///
/// <para>
/// <b>Escritura.</b> La fila es de otro Tenant y su RLS no deja escribirla con el contexto del actor; la
/// escribe <c>app_resolver_contacto_seguridad</c>, que repite la concesión, la cuenta de plataforma, el
/// estado Propuesto y los cuatro ojos, y deja la auditoría en el Tenant beneficiario.
/// </para>
/// </summary>
public record ValidarContactoSeguridadCommand(Guid ContactoId) : ICommand;

public class ValidarContactoSeguridadCommandValidator : AbstractValidator<ValidarContactoSeguridadCommand>
{
    public ValidarContactoSeguridadCommandValidator()
    {
        RuleFor(c => c.ContactoId).NotEmpty().WithMessage("Indica la propuesta que quieres validar.");
    }
}

public class ValidarContactoSeguridadCommandHandler(
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    IAutorizacionAdminPlataforma autorizacion,
    IContactosSeguridadPlataforma plataforma)
    : IRequestHandler<ValidarContactoSeguridadCommand, Result>
{
    public async Task<Result> Handle(ValidarContactoSeguridadCommand request, CancellationToken cancellationToken)
    {
        if (!await AutorizacionResolucionContactoSeguridad.PuedeAsync(
                currentUserService, sesionPrivilegiadaActual, autorizacion, cancellationToken))
            return Result.Fallo(ErroresContactoSeguridad.SoloActorDePlataforma);

        if (await AutorizacionResolucionContactoSeguridad.LaPropusoQuienResuelveAsync(
                currentUserService, plataforma, request.ContactoId, cancellationToken))
            return Result.Fallo(ErroresContactoSeguridad.MismaCuentaProponente);

        return await plataforma.ResolverAsync(request.ContactoId, motivoRechazo: null, cancellationToken);
    }
}

/// <summary>Comprobación común de validar y rechazar, antes de llegar a la base.</summary>
internal static class AutorizacionResolucionContactoSeguridad
{
    public static async Task<bool> PuedeAsync(
        ICurrentUserService currentUserService,
        ISesionPrivilegiadaActual sesionPrivilegiadaActual,
        IAutorizacionAdminPlataforma autorizacion,
        CancellationToken cancellationToken)
    {
        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not null)
            return false;

        return await currentUserService.ObtenerUsuarioActualIdAsync() is { } usuarioId
               && await autorizacion.PuedeGlobalmenteAsync(usuarioId, cancellationToken);
    }

    /// <summary>
    /// Cuatro ojos: la cuenta que propuso el contacto no lo resuelve. Se compara la cuenta autenticada
    /// (la misma que firma el contexto RLS y que la función registra como actor), no un Tenant ni otra
    /// coordenada de contexto; fuera de una Sesión Privilegiada, que <see cref="PuedeAsync"/> ya excluye, no
    /// hay Usuario simulado que pueda confundirse con el Actor real. Sin propuesta legible responde
    /// <c>false</c> y decide la función, que repite la regla (<c>misma_cuenta</c>).
    /// </summary>
    public static async Task<bool> LaPropusoQuienResuelveAsync(
        ICurrentUserService currentUserService,
        IContactosSeguridadPlataforma plataforma,
        Guid contactoId,
        CancellationToken cancellationToken) =>
        await currentUserService.ObtenerUsuarioActualIdAsync() is { } usuarioId
        && await plataforma.ObtenerCuentaProponenteAsync(contactoId, cancellationToken) == usuarioId;
}
