using CaeManager.Domain.Common;

namespace CaeManager.Domain.Retencion;

/// <summary>
/// Registro durable y mínimo de un candidato que una ejecución de
/// <see cref="SolicitudPurga"/> no pudo suprimir — ver
/// <see cref="SolicitudPurga.RegistrarResultadoEjecucion"/>. Existe para que
/// "ConIncidencias" sea correlacionable y remediable, no solo una alerta
/// operativa que puede perderse, resolverse por accidente o no llegar a
/// asociarse con la solicitud que la originó.
///
/// Guarda lo mínimo para correlacionar y remediar: a qué solicitud
/// pertenece, el identificador del registro afectado y una categoría con un
/// detalle saneado. Nunca contenido documental ni el mensaje de excepción
/// crudo — podría llevar rutas de almacenamiento u otros detalles internos;
/// el detalle completo sigue yendo al log/alerta operativa, no aquí.
/// </summary>
public class IncidenciaPurga : EntidadConTenant
{
    public const int LongitudMaximaDetalle = 500;

    public Guid SolicitudPurgaId { get; private set; }

    /// <summary>
    /// El registro afectado — un Documento o un Trabajador según
    /// <see cref="SolicitudPurga.TipoDato"/> de la solicitud referenciada.
    /// Sin FK a propósito: es polimórfico, mismo criterio que
    /// <see cref="SolicitudPurga.AutorizadaPorUsuarioId"/>.
    /// </summary>
    public Guid ObjetivoId { get; private set; }

    public TipoIncidenciaPurga Tipo { get; private set; }

    public string Detalle { get; private set; } = string.Empty;

    public DateTime DetectadaEnUtc { get; private set; } = DateTime.UtcNow;

    private IncidenciaPurga()
    {
        // Requerido por EF Core.
    }

    private IncidenciaPurga(Guid solicitudPurgaId, Guid objetivoId, TipoIncidenciaPurga tipo, string detalle)
    {
        if (solicitudPurgaId == Guid.Empty)
            throw new ArgumentException("La incidencia debe referenciar una solicitud de purga.", nameof(solicitudPurgaId));

        if (objetivoId == Guid.Empty)
            throw new ArgumentException("La incidencia debe referenciar el registro afectado.", nameof(objetivoId));

        if (string.IsNullOrWhiteSpace(detalle))
            throw new ArgumentException("La incidencia debe dejar constancia de qué ocurrió.", nameof(detalle));

        SolicitudPurgaId = solicitudPurgaId;
        ObjetivoId = objetivoId;
        Tipo = tipo;
        Detalle = detalle.Length > LongitudMaximaDetalle ? detalle[..LongitudMaximaDetalle] : detalle;
    }

    public static IncidenciaPurga Crear(Guid solicitudPurgaId, Guid objetivoId, TipoIncidenciaPurga tipo, string detalle) =>
        new(solicitudPurgaId, objetivoId, tipo, detalle);
}
