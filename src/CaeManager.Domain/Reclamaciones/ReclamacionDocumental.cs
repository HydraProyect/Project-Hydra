using CaeManager.Domain.Common;

namespace CaeManager.Domain.Reclamaciones;

/// <summary>
/// Registro de un lote de reclamación documental enviado a un Cliente —
/// agrupa varios Documentos que vencen en la misma ventana (1 a 3 meses) en
/// un único correo, en vez de uno por Documento (ver el hilo de trabajo que
/// originó esta feature: no se pueden mandar 30 correos por cada vencimiento
/// suelto). Append-only: una vez enviado no se edita ni se borra, es el
/// historial de qué se reclamó y cuándo — también evita reclamar dos veces
/// el mismo lote en poco tiempo (ver UltimaReclamacionFechaUtc en
/// ObtenerLoteReclamacionQuery).
///
/// MVP1 es solo vista previa + envío manual por el Gestor CAE — no hay
/// todavía un job en segundo plano que envíe solo (decisión explícita del
/// usuario, 2026-08-08).
/// </summary>
public class ReclamacionDocumental : EntidadBase
{
    public const int LongitudMaximaDestinatarioEmail = 500;

    private readonly List<ReclamacionDocumentalDocumento> _documentos = [];

    public Guid ClienteId { get; private set; }
    public Guid EnviadoPorUsuarioId { get; private set; }

    /// <summary>
    /// Snapshot del/los email(s) usados en el envío (varios destinatarios se
    /// unen con "; ") — no una referencia viva al usuario de portal, para que
    /// el historial no cambie de significado si ese contacto cambia de email
    /// después.
    /// </summary>
    public string DestinatarioEmail { get; private set; } = string.Empty;

    public DateTime FechaEnvioUtc { get; private set; }

    public IReadOnlyList<ReclamacionDocumentalDocumento> Documentos => _documentos.AsReadOnly();

    private ReclamacionDocumental()
    {
    }

    public ReclamacionDocumental(
        Guid clienteId, Guid enviadoPorUsuarioId, string destinatarioEmail, DateTime fechaEnvioUtc,
        IEnumerable<Guid> documentoIds)
    {
        if (clienteId == Guid.Empty)
            throw new ArgumentException("La reclamación debe pertenecer a un cliente.", nameof(clienteId));
        if (enviadoPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La reclamación debe registrar quién la envió.", nameof(enviadoPorUsuarioId));
        if (string.IsNullOrWhiteSpace(destinatarioEmail))
            throw new ArgumentException("La reclamación debe tener al menos un destinatario.", nameof(destinatarioEmail));

        var destinatarioNormalizado = destinatarioEmail.Trim();
        if (destinatarioNormalizado.Length > LongitudMaximaDestinatarioEmail)
            throw new ArgumentException(
                $"El destinatario no puede superar {LongitudMaximaDestinatarioEmail} caracteres.", nameof(destinatarioEmail));

        var idsUnicos = documentoIds.Distinct().ToList();
        if (idsUnicos.Count == 0)
            throw new ArgumentException("La reclamación debe incluir al menos un documento.", nameof(documentoIds));

        ClienteId = clienteId;
        EnviadoPorUsuarioId = enviadoPorUsuarioId;
        DestinatarioEmail = destinatarioNormalizado;
        FechaEnvioUtc = fechaEnvioUtc;

        foreach (var documentoId in idsUnicos)
            _documentos.Add(new ReclamacionDocumentalDocumento(Id, documentoId));
    }
}
