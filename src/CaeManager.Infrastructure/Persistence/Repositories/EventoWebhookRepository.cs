using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class EventoWebhookRepository(CaeManagerDbContext dbContext) : IEventoWebhookRepository
{
    public Task<EventoWebhook?> ObtenerSiguientePendienteAsync(
        ProveedorIntegracion proveedor, CancellationToken cancellationToken = default) =>
        (from evento in dbContext.EventosWebhook
         join conexion in dbContext.ConexionesIntegracion on evento.ConexionIntegracionId equals conexion.Id
         where !evento.Procesado && conexion.Proveedor == proveedor
         orderby evento.FechaRecepcionUtc
         select evento)
        .FirstOrDefaultAsync(cancellationToken);

    public void Agregar(EventoWebhook evento) => dbContext.EventosWebhook.Add(evento);
}
