using CaeManager.Domain.Common;

namespace CaeManager.Domain.Plantillas;

/// <summary>
/// Rastro de que un <see cref="Documentos.Documento"/> nació de rellenar una
/// <see cref="PlantillaDocumentoVersion"/> — común a ambos orígenes de
/// plantilla (ADR-010 § 1.3, § 3): los campos de aparato legal
/// (<see cref="CodigoSeguroVerificacion"/>/<see cref="HashSha256Impreso"/>/
/// <see cref="SelloElectronicoAplicadoEnUtc"/>) solo se rellenan para
/// <see cref="OrigenPlantilla.TalvegPropio"/> — ninguna plantilla de ese
/// origen se genera todavía (ADR-007 pendiente), así que hoy están siempre
/// vacíos. Se dejan aquí ya (en vez de añadirlos cuando ADR-007 se
/// implemente) para no duplicar esta tabla de trazabilidad más adelante —
/// tradeoff aceptado explícitamente en ADR-010 § 5.
///
/// <see cref="DatosUtilizadosJson"/> es el snapshot RGPD: qué valor concreto
/// se usó para cada campo en el momento de generar, independiente de que el
/// dato de origen (Trabajador, Empresa...) cambie después.
/// </summary>
public class DocumentoGenerado : EntidadBase
{
    public Guid PlantillaDocumentoVersionId { get; private set; }
    public Guid DocumentoId { get; private set; }

    public Guid? TrabajadorId { get; private set; }
    public Guid? EmpresaId { get; private set; }
    public Guid? CentroId { get; private set; }

    public string DatosUtilizadosJson { get; private set; } = string.Empty;
    public Guid GeneradoPorUsuarioId { get; private set; }
    public DateTime GeneradoEnUtc { get; private set; }
    public EstadoDocumentoGenerado Estado { get; private set; }

    /// <summary>Solo <see cref="OrigenPlantilla.TalvegPropio"/> — sin uso en este MVP.</summary>
    public string? CodigoSeguroVerificacion { get; private set; }

    /// <summary>Solo <see cref="OrigenPlantilla.TalvegPropio"/> — sin uso en este MVP.</summary>
    public string? HashSha256Impreso { get; private set; }

    /// <summary>Solo <see cref="OrigenPlantilla.TalvegPropio"/> — sin uso en este MVP.</summary>
    public DateTime? SelloElectronicoAplicadoEnUtc { get; private set; }

    private DocumentoGenerado()
    {
    }

    public DocumentoGenerado(
        Guid plantillaDocumentoVersionId,
        Guid documentoId,
        string datosUtilizadosJson,
        Guid generadoPorUsuarioId,
        DateTime generadoEnUtc,
        Guid? trabajadorId = null,
        Guid? empresaId = null,
        Guid? centroId = null)
    {
        if (plantillaDocumentoVersionId == Guid.Empty)
            throw new ArgumentException("El documento generado debe referenciar una versión de plantilla.", nameof(plantillaDocumentoVersionId));
        if (documentoId == Guid.Empty)
            throw new ArgumentException("El documento generado debe referenciar un Documento real.", nameof(documentoId));
        if (generadoPorUsuarioId == Guid.Empty)
            throw new ArgumentException("Quien genera debe ser un usuario válido.", nameof(generadoPorUsuarioId));

        PlantillaDocumentoVersionId = plantillaDocumentoVersionId;
        DocumentoId = documentoId;
        DatosUtilizadosJson = datosUtilizadosJson;
        GeneradoPorUsuarioId = generadoPorUsuarioId;
        GeneradoEnUtc = generadoEnUtc;
        TrabajadorId = trabajadorId;
        EmpresaId = empresaId;
        CentroId = centroId;
        Estado = EstadoDocumentoGenerado.Generado;
    }
}
