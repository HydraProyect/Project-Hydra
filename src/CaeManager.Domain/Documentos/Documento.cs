using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>Instancia de un TipoDocumento asociada a un Trabajador.</summary>
public class Documento : EntidadBase
{
    public const int LongitudMaximaArchivoUrl = 500;
    public const int LongitudMaximaComentarios = 1000;

    public Guid TrabajadorId { get; private set; }
    public Guid TipoDocumentoId { get; private set; }
    public DateOnly FechaEmision { get; private set; }
    public DateOnly? FechaVencimiento { get; private set; }
    public string? ArchivoUrl { get; private set; }
    public string? Comentarios { get; private set; }

    private Documento()
    {
    }

    public Documento(
        Guid trabajadorId,
        Guid tipoDocumentoId,
        DateOnly fechaEmision,
        DateOnly? fechaVencimiento,
        string? archivoUrl = null,
        string? comentarios = null)
    {
        if (trabajadorId == Guid.Empty)
            throw new ArgumentException("El documento debe pertenecer a un trabajador.", nameof(trabajadorId));
        if (tipoDocumentoId == Guid.Empty)
            throw new ArgumentException("El documento debe tener un tipo de documento.", nameof(tipoDocumentoId));

        TrabajadorId = trabajadorId;
        TipoDocumentoId = tipoDocumentoId;
        Renovar(fechaEmision, fechaVencimiento);
        ArchivoUrl = archivoUrl;
        Comentarios = comentarios;
    }

    /// <summary>
    /// Actualiza fecha de emisión/vencimiento — p. ej. cuando el trabajador
    /// presenta la renovación de un documento vencido. FechaVencimiento la
    /// calcula el llamador (Application) con CalculadoraEstadoDocumento,
    /// porque depende de la vigencia del TipoDocumento o de un
    /// RequisitoDocumental, que el Documento no conoce.
    /// </summary>
    public void Renovar(DateOnly fechaEmision, DateOnly? fechaVencimiento)
    {
        if (fechaEmision > DateOnly.FromDateTime(DateTime.UtcNow))
            throw new ArgumentException("La fecha de emisión no puede ser futura.", nameof(fechaEmision));
        if (fechaVencimiento is not null && fechaVencimiento < fechaEmision)
            throw new ArgumentException("La fecha de vencimiento no puede ser anterior a la de emisión.", nameof(fechaVencimiento));

        FechaEmision = fechaEmision;
        FechaVencimiento = fechaVencimiento;
    }

    public void AdjuntarArchivo(string archivoUrl)
    {
        if (string.IsNullOrWhiteSpace(archivoUrl))
            throw new ArgumentException("La URL del archivo no puede estar vacía.", nameof(archivoUrl));
        ArchivoUrl = archivoUrl;
    }

    public void ActualizarComentarios(string? comentarios) => Comentarios = comentarios;

    public EstadoDocumento CalcularEstado(DateOnly hoy, int umbralAmbarDias, int umbralRojoDias) =>
        CalculadoraEstadoDocumento.Calcular(FechaVencimiento, hoy, umbralAmbarDias, umbralRojoDias);
}
