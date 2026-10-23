using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class AlertaCriticaRepository(CaeManagerDbContext db, ITenantActual tenant) : IAlertaCriticaRepository
{
    public Task<AlertaCriticaConversacion?> ObtenerPendientePorConversacionAsync(Guid conversacionId, CancellationToken cancellationToken)
    {
        // La misma notificación puede contener varios mensajes antes de SaveChanges.
        var local = db.AlertasCriticasConversacion.Local.FirstOrDefault(a => a.ConversacionId == conversacionId
            && a.Estado == EstadoAlertaCritica.Pendiente
            && a.OperadorCaeTenantId == tenant.TenantId
            && (a.TenantId == tenant.TenantId || a.TenantId == Guid.Empty && db.Entry(a).State == EntityState.Added));
        return local is not null ? Task.FromResult<AlertaCriticaConversacion?>(local)
            : db.AlertasCriticasConversacion.FirstOrDefaultAsync(a => a.ConversacionId == conversacionId
                && a.Estado == EstadoAlertaCritica.Pendiente, cancellationToken);
    }

    // Con el índice único parcial solo se crea una alerta cuando no queda ninguna Pendiente, así que la Pendiente es además la más
    // reciente; el orden la pone primero igualmente para no depender de los relojes.
    public Task<AlertaCriticaConversacion?> ObtenerVigentePorConversacionAsync(Guid conversacionId, CancellationToken cancellationToken) =>
        db.AlertasCriticasConversacion.Where(a => a.ConversacionId == conversacionId)
            .OrderByDescending(a => a.Estado == EstadoAlertaCritica.Pendiente)
            .ThenByDescending(a => a.DetectadaEnUtc)
            .ThenByDescending(a => a.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<AlertaCriticaConversacion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.AlertasCriticasConversacion.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public void Agregar(AlertaCriticaConversacion alerta) => db.AlertasCriticasConversacion.Add(alerta);
}
