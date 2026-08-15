using CaeManager.Domain.Common;

namespace CaeManager.Domain.Plantillas;

/// <summary>Un Trabajador dentro de un <see cref="LoteGeneracionDocumento"/> — ADR-010 § 3, batch acotado a Trabajador (el caso CAE real: un formulario por persona de una lista).</summary>
public class ItemGeneracionDocumento : EntidadConTenant
{
    public const int LongitudMaximaError = 500;

    public Guid LoteGeneracionDocumentoId { get; private set; }
    public Guid TrabajadorId { get; private set; }
    public Guid? DocumentoGeneradoId { get; private set; }
    public EstadoItemGeneracion Estado { get; private set; }
    public string? Error { get; private set; }

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
