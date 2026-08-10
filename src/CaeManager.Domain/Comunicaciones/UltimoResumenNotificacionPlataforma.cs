using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Último resumen agregado conocido de una plataforma de solo resumen para un Cliente ("3
/// pendientes, 1 vencido, 0 rechazados") — ronda de reducción de ruido en Comunicaciones. Una fila
/// por (Cliente, ProveedorPlataformaCae): se sobrescribe en cada notificación nueva, no se acumula
/// histórico — solo hace falta saber "cambió algo desde la última vez", no la serie completa.
/// </summary>
public class UltimoResumenNotificacionPlataforma : EntidadConTenant
{
    public Guid ClienteId { get; private set; }
    public Guid ProveedorPlataformaCaeId { get; private set; }
    public int Pendientes { get; private set; }
    public int Vencidos { get; private set; }
    public int Rechazados { get; private set; }
    public DateTime ActualizadoEnUtc { get; private set; }

    private UltimoResumenNotificacionPlataforma()
    {
    }

    public UltimoResumenNotificacionPlataforma(
        Guid clienteId, Guid proveedorPlataformaCaeId, int pendientes, int vencidos, int rechazados, DateTime actualizadoEnUtc)
    {
        if (clienteId == Guid.Empty)
            throw new ArgumentException("El resumen debe pertenecer a un cliente.", nameof(clienteId));
        if (proveedorPlataformaCaeId == Guid.Empty)
            throw new ArgumentException("El resumen debe pertenecer a una plataforma.", nameof(proveedorPlataformaCaeId));

        ClienteId = clienteId;
        ProveedorPlataformaCaeId = proveedorPlataformaCaeId;
        Pendientes = pendientes;
        Vencidos = vencidos;
        Rechazados = rechazados;
        ActualizadoEnUtc = actualizadoEnUtc;
    }

    public bool CoincideCon(int pendientes, int vencidos, int rechazados) =>
        Pendientes == pendientes && Vencidos == vencidos && Rechazados == rechazados;

    public void Actualizar(int pendientes, int vencidos, int rechazados, DateTime actualizadoEnUtc)
    {
        Pendientes = pendientes;
        Vencidos = vencidos;
        Rechazados = rechazados;
        ActualizadoEnUtc = actualizadoEnUtc;
    }
}
