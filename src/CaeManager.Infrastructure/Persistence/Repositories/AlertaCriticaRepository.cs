using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class AlertaCriticaRepository(CaeManagerDbContext db, ITenantActual tenant) : IAlertaCriticaRepository
{
    public Task<AlertaCriticaConversacion?> ObtenerPorConversacionAsync(Guid conversacionId, CancellationToken cancellationToken)
    {
        // La misma notificación puede contener varios mensajes antes de SaveChanges.
        var local = db.AlertasCriticasConversacion.Local.FirstOrDefault(a => a.ConversacionId == conversacionId
            && a.OperadorCaeTenantId == tenant.TenantId
            && (a.TenantId == tenant.TenantId || a.TenantId == Guid.Empty && db.Entry(a).State == EntityState.Added));
        return local is not null ? Task.FromResult<AlertaCriticaConversacion?>(local)
            : db.AlertasCriticasConversacion.FirstOrDefaultAsync(a => a.ConversacionId == conversacionId, cancellationToken);
    }

    public void Agregar(AlertaCriticaConversacion alerta) => db.AlertasCriticasConversacion.Add(alerta);
}
