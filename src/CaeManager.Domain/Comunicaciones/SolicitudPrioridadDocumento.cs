using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Registro de que se pidió prioridad de validación al contacto de un
/// Centro (Fase G) — solo un rastro para poder avisar "ya se pidió hoy" y
/// evitar reenvíos duplicados sin darse cuenta; no bloquea reenviar (el
/// Gestor puede tener un motivo real para insistir, p. ej. una visita que
/// se adelantó).
/// </summary>
public class SolicitudPrioridadDocumento : EntidadConTenant
{
    public Guid CentroId { get; private set; }
    public Guid EnviadaPorUsuarioId { get; private set; }
    public DateTime EnviadaEnUtc { get; private set; }

    private SolicitudPrioridadDocumento()
    {
    }

    public SolicitudPrioridadDocumento(Guid centroId, Guid enviadaPorUsuarioId, DateTime enviadaEnUtc)
    {
        if (centroId == Guid.Empty)
            throw new ArgumentException("La solicitud debe pertenecer a un centro.", nameof(centroId));
        if (enviadaPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La solicitud debe registrar quién la envió.", nameof(enviadaPorUsuarioId));

        CentroId = centroId;
        EnviadaPorUsuarioId = enviadaPorUsuarioId;
        EnviadaEnUtc = enviadaEnUtc;
    }
}
