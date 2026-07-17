using CaeManager.Domain.Common;

namespace CaeManager.Domain.Alertas;

/// <summary>Notificación generada cuando un Documento entra en umbral de aviso o vence.</summary>
public class Alerta : Entity
{
    public Guid DocumentoId { get; private set; }
    public NivelAlerta Nivel { get; private set; }
    public DateTime FechaGeneracionUtc { get; private set; }

    private Alerta()
    {
    }

    public Alerta(Guid documentoId, NivelAlerta nivel)
    {
        if (documentoId == Guid.Empty)
            throw new ArgumentException("La alerta debe estar asociada a un documento.", nameof(documentoId));

        DocumentoId = documentoId;
        Nivel = nivel;
        FechaGeneracionUtc = DateTime.UtcNow;
    }
}
