using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Aviso pendiente generado por la verificación IA de un Documento (ver
/// VerificacionIaDocumentoService, TipoDocumento.VerificacionIaActiva): la
/// IA leyó el archivo real y encontró confianza baja, o una discrepancia
/// entre lo detectado y lo que introdujo el usuario. Nunca corrige nada
/// por sí sola — solo deja constancia de qué revisar; un Gestor CAE decide
/// si hace falta actuar y marca la revisión como hecha.
/// </summary>
public class RevisionIaDocumento : EntidadConTenant
{
    public const int LongitudMaximaTipoDetectado = 150;
    public const int LongitudMaximaMotivo = 500;

    public Guid DocumentoId { get; private set; }
    public int ConfianzaGeneral { get; private set; }
    public string? TipoDetectado { get; private set; }
    public DateOnly? FechaEmisionDetectada { get; private set; }
    public DateOnly? FechaVencimientoDetectada { get; private set; }
    public bool? TieneFirmaDetectada { get; private set; }

    /// <summary>Explicación legible de por qué se generó esta revisión (p. ej. "Confianza baja (62%); la fecha de emisión introducida no coincide con la detectada"). Construida por VerificacionIaDocumentoService, no por la IA directamente.</summary>
    public string Motivo { get; private set; } = string.Empty;

    public bool Resuelta { get; private set; }
    public DateTime CreadaEnUtc { get; private set; } = DateTime.UtcNow;

    private RevisionIaDocumento()
    {
    }

    private RevisionIaDocumento(
        Guid documentoId, int confianzaGeneral, string? tipoDetectado, DateOnly? fechaEmisionDetectada,
        DateOnly? fechaVencimientoDetectada, bool? tieneFirmaDetectada, string motivo)
    {
        if (documentoId == Guid.Empty)
            throw new ArgumentException("La revisión debe estar ligada a un documento.", nameof(documentoId));
        if (confianzaGeneral is < 0 or > 100)
            throw new ArgumentException("La confianza general debe estar entre 0 y 100.", nameof(confianzaGeneral));
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("Debe indicarse el motivo de la revisión.", nameof(motivo));

        DocumentoId = documentoId;
        ConfianzaGeneral = confianzaGeneral;
        TipoDetectado = tipoDetectado?.Length > LongitudMaximaTipoDetectado ? tipoDetectado[..LongitudMaximaTipoDetectado] : tipoDetectado;
        FechaEmisionDetectada = fechaEmisionDetectada;
        FechaVencimientoDetectada = fechaVencimientoDetectada;
        TieneFirmaDetectada = tieneFirmaDetectada;
        Motivo = motivo.Length > LongitudMaximaMotivo ? motivo[..LongitudMaximaMotivo] : motivo;
    }

    public static RevisionIaDocumento Crear(
        Guid documentoId, int confianzaGeneral, string? tipoDetectado, DateOnly? fechaEmisionDetectada,
        DateOnly? fechaVencimientoDetectada, bool? tieneFirmaDetectada, string motivo) =>
        new(documentoId, confianzaGeneral, tipoDetectado, fechaEmisionDetectada, fechaVencimientoDetectada, tieneFirmaDetectada, motivo);

    public void Resolver() => Resuelta = true;
}
