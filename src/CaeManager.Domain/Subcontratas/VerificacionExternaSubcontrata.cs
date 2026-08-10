using CaeManager.Domain.Common;

namespace CaeManager.Domain.Subcontratas;

/// <summary>
/// Registro de una comprobación manual del cumplimiento de una Subcontrata en
/// la plataforma documental del titular de un Centro (ADR-005 § 2.3): quién
/// verificó, cuándo, qué tipo documental, con qué resultado y hasta cuándo
/// declara validez la plataforma. La lista de "qué verificar" no vive aquí —
/// es <c>TipoDocumentoCentro</c> (los tipos exigidos por el Centro); esta
/// entidad solo registra hechos. El estado de supervisión derivado lo calcula
/// <see cref="CalculadoraEstadoSupervision"/> y nunca se almacena. Cuando
/// exista el conector de la Plataforma de Integraciones, alimentará este
/// mismo agregado — no se rediseña.
/// </summary>
public class VerificacionExternaSubcontrata : EntidadBase
{
    public const int LongitudMaximaObservaciones = 1000;
    public const int LongitudMaximaNombreArchivo = 260;

    public Guid SubcontrataId { get; private set; }
    public Guid CentroId { get; private set; }
    public Guid TipoDocumentoId { get; private set; }
    public DateOnly FechaVerificacion { get; private set; }
    public ResultadoVerificacionExterna Resultado { get; private set; }

    /// <summary>Fecha de validez declarada por la plataforma, si consta. Puede venir ya vencida — eso es exactamente un hallazgo, no un error.</summary>
    public DateOnly? ValidoHasta { get; private set; }

    /// <summary>Identificador opaco en IFileStorageService de la captura/justificante, si se adjuntó.</summary>
    public string? EvidenciaArchivoRuta { get; private set; }

    public string? EvidenciaNombreArchivo { get; private set; }
    public Guid UsuarioVerificadorId { get; private set; }
    public string? Observaciones { get; private set; }

    private VerificacionExternaSubcontrata()
    {
    }

    public VerificacionExternaSubcontrata(
        Guid subcontrataId,
        Guid centroId,
        Guid tipoDocumentoId,
        DateOnly fechaVerificacion,
        ResultadoVerificacionExterna resultado,
        Guid usuarioVerificadorId,
        DateOnly? validoHasta = null,
        string? observaciones = null)
    {
        if (subcontrataId == Guid.Empty)
            throw new ArgumentException("La verificación debe pertenecer a una subcontrata.", nameof(subcontrataId));
        if (centroId == Guid.Empty)
            throw new ArgumentException("La verificación debe referirse a un centro.", nameof(centroId));
        if (tipoDocumentoId == Guid.Empty)
            throw new ArgumentException("La verificación debe referirse a un tipo de documento.", nameof(tipoDocumentoId));
        if (usuarioVerificadorId == Guid.Empty)
            throw new ArgumentException("La verificación debe registrar quién verificó.", nameof(usuarioVerificadorId));
        if (fechaVerificacion > DateOnly.FromDateTime(DateTime.UtcNow))
            throw new ArgumentException("La fecha de verificación no puede ser futura.", nameof(fechaVerificacion));

        SubcontrataId = subcontrataId;
        CentroId = centroId;
        TipoDocumentoId = tipoDocumentoId;
        FechaVerificacion = fechaVerificacion;
        Resultado = resultado;
        UsuarioVerificadorId = usuarioVerificadorId;
        ValidoHasta = validoHasta;
        EstablecerObservaciones(observaciones);
    }

    /// <summary>
    /// La evidencia se adjunta tras crear la entidad porque el archivo se
    /// guarda primero en el almacenamiento (particionado por tenant) y su
    /// identificador solo existe después — mismo orden que los adjuntos de
    /// Documento.
    /// </summary>
    public void AdjuntarEvidencia(string archivoRuta, string nombreArchivo)
    {
        if (string.IsNullOrWhiteSpace(archivoRuta))
            throw new ArgumentException("La evidencia debe tener una ruta de archivo.", nameof(archivoRuta));
        if (string.IsNullOrWhiteSpace(nombreArchivo))
            throw new ArgumentException("La evidencia debe tener un nombre de archivo.", nameof(nombreArchivo));

        var nombreNormalizado = nombreArchivo.Trim();
        if (nombreNormalizado.Length > LongitudMaximaNombreArchivo)
            throw new ArgumentException(
                $"El nombre del archivo no puede superar {LongitudMaximaNombreArchivo} caracteres.", nameof(nombreArchivo));

        EvidenciaArchivoRuta = archivoRuta;
        EvidenciaNombreArchivo = nombreNormalizado;
    }

    private void EstablecerObservaciones(string? observaciones)
    {
        if (string.IsNullOrWhiteSpace(observaciones))
        {
            Observaciones = null;
            return;
        }

        var normalizadas = observaciones.Trim();
        if (normalizadas.Length > LongitudMaximaObservaciones)
            throw new ArgumentException(
                $"Las observaciones no pueden superar {LongitudMaximaObservaciones} caracteres.", nameof(observaciones));

        Observaciones = normalizadas;
    }
}
