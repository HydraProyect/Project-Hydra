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

        // Contexto desde el que se revoca: la casa de uno (el Tenant de origen, que
        // la autoridad de arriba ya exige que sea una de las dos partes) o el
        // Tenant propietario. Desde un Workspace operativo derivado de un tercero
        // se rechaza aquí, con un error propio, antes de tocar nada. Esta guarda
        // solo restringe: la autoridad ya se decidió arriba por el Tenant de
        // origen, nunca por el Tenant activo.
        var tenantPropietarioId = delegacion.TenantClienteId;
        var tenantActivo = tenantActual.TenantId;
        if (tenantActivo is null
            || (tenantActivo != tenantPropietarioId
                && tenantActivo != await currentUserService.ObtenerTenantOrigenIdAsync()))
            return Result.Fallo(Error.Crear(
                "DelegacionTenant.NoAutorizado",
                "Para revocar el acceso, vuelve a tu organización."));

        if (!delegacion.Activa)
            return Result.Fallo(Error.Crear("DelegacionTenant.YaInactiva", "Esta delegación ya estaba revocada."));

        delegacion.Desactivar();

        // La revocación escribe datos del Tenant propietario: cerrar su
        // Asignación de Operación y sus carteras. La política WITH CHECK de esa
        // tabla exige que el Tenant activo sea el propietario, y el Operador CAE
        // externo que renuncia desde su propia casa tiene activo el suyo. El
        // guardado se hace por eso en el ámbito del Tenant propietario, mismo
        // mecanismo que CrearClienteDeleganteCommand. No amplía la autoridad: las
        // dos partes ya podían revocar (ver DelegacionAdministrablePorElUsuarioAsync)
        // y revocar solo reduce capacidad.
        using (AmbitoTenantExplicito.Establecer(tenantPropietarioId))
        {
            // Solo las de Operador externo tienen operación que cerrar: las de
            // Soporte son plano 3 y conservan su mecánica propia hasta su fase
            // (ADR-011 § 8.6). Cerrar la operación arrastra sus carteras.
            if (delegacion.Proposito == PropositoDelegacion.OperadorExterno)
                await asignacionesWriter.CerrarOperacionDelegadaAsync(
                    tenantPropietarioId, delegacion.TenantConsultoraId,
                    MotivoCierreAsignacion.Revocada, cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

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
    /// delegaciones de terceros sobre él. Administrar delegaciones se hace
    /// desde la casa de uno (o desde el Tenant propietario): el Tenant activo no
    /// concede autoridad, el comando solo lo usa después para rechazar el
    /// Workspace operativo derivado de un tercero.
    /// </summary>
    internal static async Task<bool> DelegacionAdministrablePorElUsuarioAsync(
        DelegacionTenant delegacion, ICurrentUserService currentUserService)
    {
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();

        return tenantOrigenId is not null &&
               (delegacion.TenantConsultoraId == tenantOrigenId.Value || delegacion.TenantClienteId == tenantOrigenId.Value);
    }
}
