using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

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
///
/// <para>
/// Quién revoca la de un Operador CAE externo: solo su Gestor CAE
/// (<see cref="GestorCaeDelOperadorCaeExterno"/>, decisión de producto del 2026-10-26). El
/// Administrador del Tenant propietario no.
/// </para>
/// </summary>
public record DesactivarDelegacionTenantCommand(Guid DelegacionTenantId) : ICommand;

/// <summary>
/// ¿Puede el usuario actual revocar este acceso ahora? Expone a la pantalla EXACTAMENTE la
/// autoridad que <see cref="DesactivarDelegacionTenantCommand"/> exige, más que siga activo:
/// el botón «Revocar acceso» se pinta solo a quien puede usarlo. Solo responde por las de
/// Operador CAE externo; las de Soporte tienen su propio botón y su propio criterio.
/// </summary>
/// <param name="Id">La delegación (Asignación de Operación delegada) sobre la que se pregunta.</param>
public record PuedeRevocarQuery(Guid Id) : IRequest<bool>;

public class DesactivarDelegacionTenantCommandValidator : AbstractValidator<DesactivarDelegacionTenantCommand>
{
    public DesactivarDelegacionTenantCommandValidator()
    {
        RuleFor(c => c.DelegacionTenantId).NotEmpty().WithMessage("Indica la delegación que quieres revocar.");
    }
}

public class DesactivarDelegacionTenantCommandHandler(
    IDelegacionTenantRepository repositorio, ICurrentUserService currentUserService,
    IAsignacionesOperativasWriter asignacionesWriter, IUnitOfWork unitOfWork, ITenantActual tenantActual,
    IDirectorioUsuariosService directorioUsuarios, ITenantsQueryContext tenantsContext)
    : IRequestHandler<DesactivarDelegacionTenantCommand, Result>,
      IRequestHandler<PuedeRevocarQuery, bool>
{
    public async Task<bool> Handle(PuedeRevocarQuery request, CancellationToken cancellationToken)
    {
        var acceso = await tenantsContext.DelegacionesTenant
            .Where(d => d.Id == request.Id)
            .Select(d => new { d.TenantConsultoraId, d.Proposito, d.Activa })
            .SingleOrDefaultAsync(cancellationToken);

        return acceso is { Proposito: PropositoDelegacion.OperadorExterno, Activa: true }
               && await GestorCaeDelOperadorCaeExterno.EsAsync(
                   acceso.TenantConsultoraId, currentUserService, directorioUsuarios, cancellationToken);
    }

    public async Task<Result> Handle(DesactivarDelegacionTenantCommand request, CancellationToken cancellationToken)
    {
        var delegacion = await repositorio.ObtenerPorIdAsync(request.DelegacionTenantId, cancellationToken);

        // "No encontrada" y no "no autorizado": confirmar la existencia de la fila ya revelaría
        // qué Operadores CAE externos operan sobre qué Tenants propietarios.
        if (delegacion is null || !await PuedeRevocarAsync(delegacion, cancellationToken))
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
        // tabla exige que el Tenant activo sea el propietario, y el Gestor CAE del
        // Operador CAE externo que revoca desde su propia casa tiene activo el suyo. El
        // guardado se hace por eso en el ámbito del Tenant propietario, mismo
        // mecanismo que CrearClienteDeleganteCommand. No amplía la autoridad, que
        // ya se decidió arriba (PuedeRevocarAsync), y revocar solo reduce capacidad.
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
    /// La de un Operador CAE externo, solo su Gestor CAE
    /// (<see cref="GestorCaeDelOperadorCaeExterno"/>). La de Soporte TALVEG (plano 3) conserva
    /// su criterio anterior, las dos partes (<see cref="DelegacionAdministrablePorElUsuarioAsync"/>):
    /// la decisión del 2026-10-26 habla del Operador CAE externo, y el acceso de Soporte se
    /// cierra por su propio camino (<c>CerrarAccesoSoporteCommand</c>).
    /// </summary>
    private Task<bool> PuedeRevocarAsync(DelegacionTenant delegacion, CancellationToken cancellationToken) =>
        delegacion.Proposito == PropositoDelegacion.OperadorExterno
            ? GestorCaeDelOperadorCaeExterno.EsAsync(
                delegacion.TenantConsultoraId, currentUserService, directorioUsuarios, cancellationToken)
            : DelegacionAdministrablePorElUsuarioAsync(delegacion, currentUserService);

    /// <summary>
    /// Las dos partes: el Tenant de origen del usuario es el que presta el servicio o el
    /// dueño de los datos, sin mirar rol. <b>Ya no decide la revocación ni la reactivación de
    /// la Asignación de Operación de un Operador CAE externo</b>: esa es solo de su Gestor CAE
    /// (<see cref="GestorCaeDelOperadorCaeExterno"/>, decisión del 2026-10-26). Queda para lo
    /// que la decisión no cubre: revocar una de Soporte (ver <see cref="PuedeRevocarAsync"/>) y
    /// retirar a una persona concreta sin cortar el acceso entero
    /// (<c>RevocarAsignacionOperadorDelegadoCommand</c>).
    ///
    /// <para>
    /// Se decide con el tenant de <b>origen</b>, nunca con
    /// <c>ITenantActual.TenantId</c>: ese refleja el Delegated Workspace
    /// activo, así que un Operador Delegado que estuviera operando como el
    /// cliente pasaría por parte de ese cliente y podría tocar
    /// delegaciones de terceros sobre él. El Tenant activo no concede
    /// autoridad; el comando solo lo usa después para rechazar el
    /// Workspace operativo derivado de un tercero.
    /// </para>
    /// </summary>
    internal static async Task<bool> DelegacionAdministrablePorElUsuarioAsync(
        DelegacionTenant delegacion, ICurrentUserService currentUserService)
    {
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();

        return tenantOrigenId is not null &&
               (delegacion.TenantConsultoraId == tenantOrigenId.Value || delegacion.TenantClienteId == tenantOrigenId.Value);
    }
}
