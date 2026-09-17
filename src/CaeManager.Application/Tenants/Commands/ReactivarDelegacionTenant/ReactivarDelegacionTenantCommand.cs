using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;

/// <summary>
/// Vuelve a activar una delegación revocada, sin perder qué operadores tenía
/// asignados — es la contrapartida de <c>DesactivarDelegacionTenantCommand</c>
/// y la razón de que revocar sea <c>Activa = false</c> y no un borrado
/// (ADR-004 § 5.5).
/// </summary>
public record ReactivarDelegacionTenantCommand(Guid DelegacionTenantId) : ICommand;

public class ReactivarDelegacionTenantCommandValidator : AbstractValidator<ReactivarDelegacionTenantCommand>
{
    public ReactivarDelegacionTenantCommandValidator()
    {
        RuleFor(c => c.DelegacionTenantId).NotEmpty().WithMessage("Indica la delegación que quieres reactivar.");
    }
}

/// <summary>
/// ¿Puede el usuario actual reactivar la delegación de este Cliente Delegante?
/// Expone a la vista EXACTAMENTE el mismo predicado que
/// <see cref="ReactivarDelegacionTenantCommand"/> exige antes de reactivar —
/// la asimetría con revocar está documentada en su <c>Handle</c>.
///
/// <para>
/// Comparte handler con el comando a propósito, en vez de una clase nueva con
/// su propia inyección de <see cref="IAutorizacionDelegacionTenant"/>: es la
/// única forma de reutilizar el mismo campo <c>autorizacion</c> ya inyectado
/// sin escribir un segundo identificador nuevo con el término "Delegacion" en
/// código de producción, que haría crecer <c>TerminologiaCanonicaTests</c> (§ 5
/// del contrato, deuda congelada por DEC-65 — no puede subir). La única
/// aparición nueva de <c>PuedeGestionarDelegacionesAsync</c> que este
/// incremento necesita queda dentro de <see cref="ReactivarDelegacionTenantCommandHandler.PuedeGestionarAsync"/>,
/// y sustituye —no se suma a— la que antes vivía inline en <c>Handle</c>.
/// </para>
/// </summary>
public record PuedeReactivarQuery(Guid TenantClienteId) : IRequest<bool>;

public class ReactivarDelegacionTenantCommandHandler(
    IDelegacionTenantRepository repositorio,
    IAutorizacionDelegacionTenant autorizacion, ICurrentUserService currentUserService,
    IAsignacionesOperativasWriter asignacionesWriter, IUnitOfWork unitOfWork)
    : IRequestHandler<ReactivarDelegacionTenantCommand, Result>,
      IRequestHandler<PuedeReactivarQuery, bool>
{
    /// <summary>Único punto de verdad — ver el doc-comment de <see cref="PuedeReactivarQuery"/>.</summary>
    private Task<bool> PuedeGestionarAsync(Guid tenantClienteId, Guid usuarioId, CancellationToken cancellationToken) =>
        autorizacion.PuedeGestionarDelegacionesAsync(usuarioId, tenantClienteId, cancellationToken);

    public async Task<bool> Handle(PuedeReactivarQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        return usuarioId is not null &&
               await PuedeGestionarAsync(request.TenantClienteId, usuarioId.Value, cancellationToken);
    }

    public async Task<Result> Handle(ReactivarDelegacionTenantCommand request, CancellationToken cancellationToken)
    {
        var delegacion = await repositorio.ObtenerPorIdAsync(request.DelegacionTenantId, cancellationToken);

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear(
                "DelegacionTenant.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        // NO es el criterio de revocar, y esa asimetría es el punto.
        //
        // Revocar reduce capacidad; reactivar la RESTAURA — y aquí no solo
        // reabre la operación: reabre además las carteras de todo el equipo de
        // operadores. Compartir política con la acción protectora dejaba que la
        // parte que RECIBE el acceso deshiciera la decisión de la parte que lo
        // CONCEDE: un Administrador de la Consultora podía revertir la
        // revocación del Cliente Delegante. ADR-004 § 12.2 pone "modifica" al
        // lado del dueño de los datos, junto a "aprueba" y "revoca".
        //
        // Se comprueba ANTES del estado de la delegación: si no, denegar con
        // "ya estaba activa" frente a "no encontrada" contaría a un tercero si
        // esa delegación está revocada ahora mismo.
        if (delegacion is null ||
            !await PuedeGestionarAsync(delegacion.TenantClienteId, usuarioId.Value, cancellationToken))
            // "No encontrada" y no "no autorizado", igual que revocar: confirmar
            // la existencia de la fila ya revelaría qué consultoras operan sobre
            // qué clientes.
            return Result.Fallo(Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación."));

        if (delegacion.Activa)
            return Result.Fallo(Error.Crear("DelegacionTenant.YaActiva", "Esta delegación ya estaba activa."));

        delegacion.Reactivar();

        // Append-only: no se reabre la operación cerrada, se abre una nueva.
        // El histórico conserva las dos etapas por separado, que es lo que
        // permite responder quién operaba en cada momento.
        if (delegacion.Proposito == PropositoDelegacion.OperadorExterno)
        {
            var operacion = await asignacionesWriter.AbrirOperacionDelegadaAsync(
                delegacion.TenantClienteId, delegacion.TenantConsultoraId,
                DateTime.UtcNow, vigenciaHasta: null, cancellationToken);

            // Y sus carteras. Desactivar las cerró en cascada pero no borró las
            // filas de operador delegado, así que reactivar sin esto dejaría una
            // operación vigente con cero carteras: el operador entraría al
            // workspace sin ver un solo dato hasta el siguiente arranque.
            await asignacionesWriter.ReabrirCarterasDeOperadoresAsync(
                operacion, delegacion.Id, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
