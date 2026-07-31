namespace CaeManager.Domain.Retencion;

/// <summary>
/// Qué categoría de dato personal propone destruir una
/// <see cref="SolicitudPurga"/>. Cada una tiene su propio plazo y su propio
/// evento de inicio, configurables (ver <c>RetencionDatosOptions</c>).
/// </summary>
public enum TipoDatoPurgable
{
    /// <summary>
    /// Documentos cumplido su plazo. El cómputo arranca en el evento más
    /// temprano entre el fin de vigencia y el cese de la relación, o en la
    /// emisión si el documento no vence — ver
    /// <c>CalculadoraRetencionDocumento</c>.
    /// </summary>
    Documentos,

    /// <summary>
    /// Trabajadores dados de baja y su información asociada. El cómputo
    /// arranca el día de la baja (decisión del propietario del producto,
    /// 2026-07-31).
    ///
    /// Es la categoría más sensible: incluye datos de salud procedentes de
    /// reconocimientos médicos, por lo que la purga se resuelve como
    /// anonimización irreversible y no como borrado — conserva el histórico
    /// de auditoría CAE dejando de ser dato personal.
    /// </summary>
    TrabajadoresDadosDeBaja
}
