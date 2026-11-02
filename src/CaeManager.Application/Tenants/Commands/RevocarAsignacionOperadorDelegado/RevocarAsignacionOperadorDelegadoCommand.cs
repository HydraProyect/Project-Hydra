using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Tenants.Commands.RevocarAsignacionOperadorDelegado;

/// <summary>
/// Retira a un Operador Delegado concreto de un Delegated Workspace, sin
/// revocar la delegación entera — el caso corriente de una persona que deja
/// la Consultora o cambia de cartera. Sin esto, la única forma de quitarle el
/// acceso era revocar la delegación completa, que afecta a todo el equipo
/// (hallazgo N-4 de Project-Hydra-Negocio/seguridad/INFORME-AUDITORIA-2.md).
///
/// <para>
/// Quién retira por este comando a una persona de un Operador CAE externo: solo el Administrador
/// del Tenant propietario, desde su casa (<see cref="AdministradorQueReactiva"/>; decisión de Chris
/// del 2026-10-26 y ADR-004 § 12.2). Es el mismo predicado que reactiva su acceso, y la misma
/// autoridad que lo autoriza: decide qué persona entra en los datos y cierra su cartera, así que un
/// Gestor CAE del Tenant propietario no lo hace aunque RLS y la escritura general se lo admitan. La
/// de Soporte TALVEG (plano 3) conserva las dos partes como criterio
/// (<c>DelegacionAdministrablePorElUsuarioAsync</c>). Las rutas de cartera del plano de Operación
/// (<c>AsignarCarteraGestorCaeCommand</c>, <c>RevocarIncorporacionCarteraCommand</c>,
/// <c>RetirarApoyoConcedidoCommand</c>) y la revocación entera siguen en el equipo del Operador CAE
/// externo: este comando no las gobierna.
/// </para>
/// </summary>
public record RevocarAsignacionOperadorDelegadoCommand(Guid AsignacionId) : ICommand;

public class RevocarAsignacionOperadorDelegadoCommandValidator : AbstractValidator<RevocarAsignacionOperadorDelegadoCommand>
{
    public RevocarAsignacionOperadorDelegadoCommandValidator()
    {
        RuleFor(c => c.AsignacionId).NotEmpty().WithMessage("Indica el operador que quieres retirar.");
    }
}

public class RevocarAsignacionOperadorDelegadoCommandHandler(
    IAsignacionOperadorDelegadoRepository repositorio,
    IDelegacionTenantRepository delegacionRepositorio,
    ICurrentUserService currentUserService,
    IAsignacionesOperativasWriter asignacionesWriter,
    IUnitOfWork unitOfWork,
    ITenantActual tenantActual,
    IAutorizacionDelegacionTenant autorizacion)
    : IRequestHandler<RevocarAsignacionOperadorDelegadoCommand, Result>
{
    public async Task<Result> Handle(RevocarAsignacionOperadorDelegadoCommand request, CancellationToken cancellationToken)
    {
        var asignacion = await repositorio.ObtenerPorIdAsync(request.AsignacionId, cancellationToken);
        if (asignacion is null)
            return Result.Fallo(Error.Crear("AsignacionOperadorDelegado.NoEncontrada", "No encontramos esa asignación."));

        // La asignación no dice por sí sola de qué dos tenants se trata: la
        // autorización cuelga de su delegación.
        var delegacion = await delegacionRepositorio.ObtenerPorIdAsync(asignacion.DelegacionTenantId, cancellationToken);

        if (delegacion is null ||
            !await DesactivarDelegacionTenantCommandHandler.DelegacionAdministrablePorElUsuarioAsync(delegacion, currentUserService))
            return Result.Fallo(Error.Crear("AsignacionOperadorDelegado.NoEncontrada", "No encontramos esa asignación."));

        // El Tenant de origen abre la lectura de la fila, pero borrarla exige que el
        // Tenant activo sea un extremo de la delegación. Desde el Workspace operativo
        // derivado de un tercero el DELETE no alcanzaría ninguna fila y EF lo convertiría
        // en un DbUpdateConcurrencyException: se rechaza aquí, antes de tocar nada. Esta
        // guarda solo restringe; la autoridad sale del Tenant de origen, nunca del activo.
        var tenantPropietarioId = delegacion.TenantClienteId;
        var tenantActivo = tenantActual.TenantId;
        if (tenantActivo != tenantPropietarioId && tenantActivo != delegacion.TenantConsultoraId)
            return Result.Fallo(Error.Crear(
                "AsignacionOperadorDelegado.NoAutorizado",
                "Para retirar a esta persona, vuelve a tu organización."));

        // La de un Operador CAE externo cierra además su cartera, que es dato del Tenant
        // propietario: desde la casa del Operador CAE externo posicion_en_la_asignacion deja
        // leerla pero no escribirla, y el guardado fallaría entero. Se rechaza antes de
        // tocar nada, tenga o no cartera vigente, para que la regla no dependa del dato.
        var deOperadorExterno = delegacion.Proposito == PropositoDelegacion.OperadorExterno;
        if (deOperadorExterno && tenantActivo != tenantPropietarioId)
            return Result.Fallo(Error.Crear(
                "AsignacionOperadorDelegado.RequiereTenantPropietario",
                "Pide a la organización cuyos datos gestiona que retire a esta persona."));

        // Estar en un extremo solo dice que la fila no es ajena. Retirar a una persona de un
        // Operador CAE externo le interrumpe el acceso y cierra su cartera: es del plano de
        // Propiedad y de su Administrador, con el mismo predicado que reactivar. Soporte TALVEG
        // no pasa por aquí: su criterio sigue siendo el de las dos partes, ya comprobado arriba.
        if (deOperadorExterno
            && !await AdministradorQueReactiva.PuedeAsync(
                tenantPropietarioId, currentUserService, tenantActual, autorizacion, cancellationToken))
            return Result.Fallo(Error.Crear(
                "AsignacionOperadorDelegado.NoAutorizado",
                "Solo un Administrador de la organización propietaria de los datos puede retirar a esta persona."));

        repositorio.Eliminar(asignacion);

        // La fila antigua se borra; la cartera se CIERRA. La diferencia es
        // deliberada: el modelo nuevo conserva el rastro de quién operó qué y
        // hasta cuándo, y borrarla perdería exactamente eso.
        if (deOperadorExterno)
            await asignacionesWriter.CerrarCarteraOperadorAsync(
                tenantPropietarioId, delegacion.TenantConsultoraId,
                asignacion.UsuarioId, MotivoCierreAsignacion.Revocada, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
