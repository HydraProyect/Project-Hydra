using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
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
/// (hallazgo N-4 de Project-Hydra-Negocio/seguridad/INFORME-AUDITORIA-2.md), que es justo lo contrario de lo
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
    IDelegacionTenantRepository repositorio, ICurrentUserService currentUserService,
    IAsignacionesOperativasWriter asignacionesWriter, IUnitOfWork unitOfWork, ITenantActual tenantActual)
    : IRequestHandler<DesactivarDelegacionTenantCommand, Result>
{
    public async Task<Result> Handle(DesactivarDelegacionTenantCommand request, CancellationToken cancellationToken)
    {
        var delegacion = await repositorio.ObtenerPorIdAsync(request.DelegacionTenantId, cancellationToken);

        if (delegacion is null || !await DelegacionAdministrablePorElUsuarioAsync(delegacion, currentUserService))
            return Result.Fallo(Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación."));

        // Cerrar la AsignacionOperacion de una delegación de Operador externo es
        // escribir datos del Tenant propietario: la política WITH CHECK de
        // AsignacionesOperacion exige que ese sea el Tenant activo. Desde otro
        // Workspace operativo derivado se rechaza aquí, con un error propio,
        // antes de tocar nada. Esta guarda solo restringe: la autoridad ya se
        // decidió arriba por el Tenant de origen. Las de Soporte no escriben
        // en AsignacionesOperacion y conservan su comportamiento.
        if (delegacion.Proposito == PropositoDelegacion.OperadorExterno
            && tenantActual.TenantId != delegacion.TenantClienteId)
            return Result.Fallo(Error.Crear(
                "DelegacionTenant.NoAutorizado",
                "Para revocar el acceso, entra en la organización que delega."));

        if (!delegacion.Activa)
            return Result.Fallo(Error.Crear("DelegacionTenant.YaInactiva", "Esta delegación ya estaba revocada."));

        delegacion.Desactivar();

        // Solo las de Operador externo tienen operación que cerrar: las de
        // Soporte son plano 3 y conservan su mecánica propia hasta su fase
        // (ADR-011 § 8.6). Cerrar la operación arrastra sus carteras.
        if (delegacion.Proposito == PropositoDelegacion.OperadorExterno)
            await asignacionesWriter.CerrarOperacionDelegadaAsync(
                delegacion.TenantClienteId, delegacion.TenantConsultoraId,
                MotivoCierreAsignacion.Revocada, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }

    /// <summary>
    /// Ambas partes pueden revocar: la Consultora que presta el servicio y el
    /// Cliente Delegante dueño de los datos. Cortar el acceso es la acción
    /// protectora — dejarla solo en manos de quien recibe el acceso es
    /// precisamente el defecto que se está cerrando.
    ///
    /// <para>
    /// <b>Esta política vale para revocar y solo para revocar.</b> Las
    /// operaciones que crean o restauran autoridad —crear la delegación,
    /// asignar un operador, reactivar— exigen ser <c>Administrador</c> del
    /// Cliente Delegante (<see cref="IAutorizacionDelegacionTenant"/>,
    /// ADR-004 § 12.2). La simetría revocar/reactivar es falsa desde
    /// seguridad: una reduce capacidad y la otra la devuelve. La comparte
    /// <c>RevocarAsignacionOperadorDelegadoCommand</c>, que también corta.
    /// </para>
    ///
    /// Se decide con el tenant de <b>origen</b>, nunca con
    /// <c>ITenantActual.TenantId</c>: ese refleja el Delegated Workspace
    /// activo, así que un Operador Delegado que estuviera operando como el
    /// cliente pasaría por administrador de ese cliente y podría tocar
    /// delegaciones de terceros sobre él. El Tenant activo no concede autoridad;
    /// el comando solo lo usa, después, para exigir que una delegación de
    /// Operador externo se revoque con el Tenant propietario activo, que es
    /// donde RLS deja cerrar su operación.
    /// </summary>
    internal static async Task<bool> DelegacionAdministrablePorElUsuarioAsync(
        DelegacionTenant delegacion, ICurrentUserService currentUserService)
    {
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();

        return tenantOrigenId is not null &&
               (delegacion.TenantConsultoraId == tenantOrigenId.Value || delegacion.TenantClienteId == tenantOrigenId.Value);
    }
}
