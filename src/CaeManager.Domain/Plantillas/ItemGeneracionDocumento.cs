using CaeManager.Domain.Common;

namespace CaeManager.Domain.Plantillas;

/// <summary>Un Trabajador dentro de un <see cref="LoteGeneracionDocumento"/> — ADR-010 § 3, batch acotado a Trabajador (el caso CAE real: un formulario por persona de una lista).</summary>
public class ItemGeneracionDocumento : EntidadConTenant, IVersionable
{
    public const int LongitudMaximaError = 500;

    public Guid LoteGeneracionDocumentoId { get; private set; }
    public Guid TrabajadorId { get; private set; }
    public Guid? DocumentoGeneradoId { get; private set; }
    public EstadoItemGeneracion Estado { get; private set; }
    public string? Error { get; private set; }

    /// <summary>
    /// Token de concurrencia optimista (auditoría de seguridad del módulo,
    /// 2026-08-30): la generación en lote se ejecuta ítem a ítem dentro del
    /// mismo circuito Blazor síncrono (ADR-010 § 2.6), pero dos pestañas del
    /// mismo lote —o un reintento— podían procesar el mismo ítem Pendiente a
    /// la vez sin que nada lo detectara. <see cref="IVersionable"/> y no
    /// heredar de <c>EntidadBase</c>: esta entidad no necesita soft delete ni
    /// timestamp de auditoría, solo el token (mismo criterio que
    /// <c>AsignacionResponsabilidad</c>).
    /// </summary>
    public Guid Version { get; private set; } = Guid.NewGuid();

    private ItemGeneracionDocumento()
    {
    }

    public ItemGeneracionDocumento(Guid loteGeneracionDocumentoId, Guid trabajadorId)
    {
        if (loteGeneracionDocumentoId == Guid.Empty)
            throw new ArgumentException("El elemento debe pertenecer a un lote.", nameof(loteGeneracionDocumentoId));
        if (trabajadorId == Guid.Empty)
            throw new ArgumentException("El elemento debe referenciar un trabajador.", nameof(trabajadorId));

        LoteGeneracionDocumentoId = loteGeneracionDocumentoId;
        TrabajadorId = trabajadorId;
        Estado = EstadoItemGeneracion.Pendiente;
    }

    public void MarcarCompletado(Guid documentoGeneradoId)
    {
        RequerirPendiente();
        if (documentoGeneradoId == Guid.Empty)
            throw new ArgumentException("El documento generado no puede estar vacío.", nameof(documentoGeneradoId));

        DocumentoGeneradoId = documentoGeneradoId;
        Estado = EstadoItemGeneracion.Completado;
    }

    public void MarcarFallido(string error)
    {
        RequerirPendiente();
        Error = string.IsNullOrWhiteSpace(error)
            ? "Fallo desconocido."
            : error.Length > LongitudMaximaError ? error[..LongitudMaximaError] : error;
        Estado = EstadoItemGeneracion.Fallido;
    }

    private void RequerirPendiente()
    {
        if (Estado != EstadoItemGeneracion.Pendiente)
            throw new InvalidOperationException("Este elemento ya se procesó — no se puede volver a marcar.");
    }
}
