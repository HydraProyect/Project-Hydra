using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Integraciones;

public interface IIntegracionesQueryContext
{
    IQueryable<ConexionIntegracion> ConexionesIntegracion { get; }
    IQueryable<LineaWhatsApp> LineasWhatsApp { get; }
    IQueryable<MiembroPoolLinea> MiembrosPoolLinea { get; }

    /// <summary>Cola de ingesta, para los avisos de Conexiones (eventos retenidos o descartados).</summary>
    IQueryable<EventoWebhook> EventosWebhook { get; }
}
