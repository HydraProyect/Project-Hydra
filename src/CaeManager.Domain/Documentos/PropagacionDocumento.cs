using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>Entrega de una versión aprobada a un canal de un Centro del mismo Tenant propietario.</summary>
public sealed class PropagacionDocumento : EntidadBase
{
    public Guid DocumentoId { get; private set; }
    public Guid CanalGestionDocumentalId { get; private set; }
    public Guid? HechoPorUsuarioId { get; private set; }
    public DateTime? HechoEnUtc { get; private set; }
    public Guid? ConversacionId { get; private set; }
    public bool Hecha => HechoEnUtc is not null;

    private PropagacionDocumento() { }

    public PropagacionDocumento(Guid documentoId, Guid canalGestionDocumentalId)
    {
        if (documentoId == Guid.Empty || canalGestionDocumentalId == Guid.Empty)
            throw new ArgumentException("La propagación necesita un documento y un canal de entrega.");
        DocumentoId = documentoId;
        CanalGestionDocumentalId = canalGestionDocumentalId;
    }

    public void MarcarHecha(Guid usuarioId, DateTime ahoraUtc, Guid? conversacionId = null)
    {
        if (usuarioId == Guid.Empty || ahoraUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Indica quién hizo la entrega y cuándo, en UTC.");
        HechoPorUsuarioId = usuarioId;
        HechoEnUtc = ahoraUtc;
        ConversacionId = conversacionId;
    }

    public void Deshacer(Guid usuarioId)
    {
        if (ConversacionId is not null)
            throw new InvalidOperationException("Un correo enviado no se puede deshacer.");
        if (HechoPorUsuarioId != usuarioId)
            throw new InvalidOperationException("Solo quien marcó la entrega puede deshacerla.");
        HechoPorUsuarioId = null;
        HechoEnUtc = null;
    }
}
