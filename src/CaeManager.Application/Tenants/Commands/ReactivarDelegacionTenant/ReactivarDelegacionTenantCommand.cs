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

public class ReactivarDelegacionTenantCommandHandler(
    IDelegacionTenantRepository repositorio,
    IAutorizacionDelegacionTenant autorizacion, ICurrentUserService currentUserService,
    IAsignacionesOperativasWriter asignacionesWriter, IUnitOfWork unitOfWork)
    : IRequestHandler<ReactivarDelegacionTenantCommand, Result>
{
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
            !await autorizacion.PuedeGestionarDelegacionesAsync(
                usuarioId.Value, delegacion.TenantClienteId, cancellationToken))
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
        if (delegacion.Proposito == PropositoDelegacion.Comercial)
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
