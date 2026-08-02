using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;

/// <summary>
/// Revoca un Delegated Workspace (ADR-004 § 5.5, Escenario 4): la Consultora
/// deja de poder operar sobre el Cliente Delegante. No mueve ni borra una sola
/// fila de datos operativos — nunca fueron suyas.
///
/// Desactiva, no borra, siguiendo la regla explícita de <see cref="DelegacionTenant"/>:
/// conserva el histórico de qué Consultora operó sobre qué tenant y cuándo.
///
/// Sin esto la delegación era irrevocable por cualquier camino del producto
/// (hallazgo N-4 de INFORME-AUDITORIA-2.md), que es justo lo contrario de lo
/// que promete el titular del ADR — un modelo de delegación reversible — y de
/// lo que un responsable del tratamiento debe poder garantizar.
/// </summary>
public record DesactivarDelegacionTenantCommand(Guid DelegacionTenantId) : ICommand;

public class DesactivarDelegacionTenantCommandValidator : AbstractValidator<DesactivarDelegacionTenantCommand>
{
    public DesactivarDelegacionTenantCommandValidator()
    {
        RuleFor(c => c.DelegacionTenantId).NotEmpty().WithMessage("Indica la delegación que quieres revocar.");
    }
}

public class DesactivarDelegacionTenantCommandHandler(
    IDelegacionTenantRepository repositorio, ICurrentUserService currentUserService, IUnitOfWork unitOfWork)
    : IRequestHandler<DesactivarDelegacionTenantCommand, Result>
{
    public async Task<Result> Handle(DesactivarDelegacionTenantCommand request, CancellationToken cancellationToken)
    {
        var delegacion = await repositorio.ObtenerPorIdAsync(request.DelegacionTenantId, cancellationToken);

        if (delegacion is null || !await DelegacionAdministrablePorElUsuarioAsync(delegacion, currentUserService))
            return Result.Fallo(Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación."));

        if (!delegacion.Activa)
            return Result.Fallo(Error.Crear("DelegacionTenant.YaInactiva", "Esta delegación ya estaba revocada."));

        delegacion.Desactivar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }

    /// <summary>
    /// Ambas partes pueden revocar: la Consultora que presta el servicio y el
    /// Cliente Delegante dueño de los datos. Cortar el acceso es la acción
    /// protectora — dejarla solo en manos de quien recibe el acceso es
    /// precisamente el defecto que se está cerrando. Crear la delegación es
    /// otra cosa y sigue sin flujo de producto: el ADR-004 § 12.2 deja abierto
    /// a propósito quién puede iniciarla, por sus implicaciones comerciales.
    ///
    /// Se decide con el tenant de <b>origen</b>, nunca con
    /// <c>ITenantActual.TenantId</c>: ese refleja el Delegated Workspace
    /// activo, así que un Operador Delegado que estuviera operando como el
    /// cliente pasaría por administrador de ese cliente y podría tocar
    /// delegaciones de terceros sobre él. Administrar delegaciones se hace
    /// siempre desde la casa de uno.
    /// </summary>
    internal static async Task<bool> DelegacionAdministrablePorElUsuarioAsync(
        DelegacionTenant delegacion, ICurrentUserService currentUserService)
    {
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();

        return tenantOrigenId is not null &&
               (delegacion.TenantConsultoraId == tenantOrigenId.Value || delegacion.TenantClienteId == tenantOrigenId.Value);
    }
}
