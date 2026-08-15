namespace CaeManager.Domain.DocumentosIa;

public interface IAuditoriaExtraccionIaRepository
{
    void Agregar(AuditoriaExtraccionIa auditoria);

    /// <summary>
    /// La auditoría más reciente ligada a este Documento que todavía no
    /// tiene decisión humana registrada — usada para cerrar el ciclo
    /// "la IA propone, el humano dispone" cuando se resuelve una
    /// RevisionIaDocumento o se aprueba automáticamente (ver
    /// VerificacionIaDocumentoService, ResolverRevisionIaDocumentoCommand,
    /// AplicarDeteccionIaDocumentoCommand). Null si no hay ninguna — p. ej.
    /// un Documento creado antes de que existiera este enlace, o cuya
    /// extracción vino solo del triage previo a su creación.
    /// </summary>
    Task<AuditoriaExtraccionIa?> ObtenerUltimaSinDecisionPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default);
}
