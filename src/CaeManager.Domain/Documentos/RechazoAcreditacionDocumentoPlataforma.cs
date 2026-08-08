using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Una entrada del historial de rechazos de una <see cref="AcreditacionDocumentoPlataforma"/>
/// (docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (b)) — append-only: un
/// rechazo nuevo nunca sobreescribe ni borra los anteriores, es la invariante
/// explícita del plan ("los rechazos anteriores se conservan como historial").
/// Solo <see cref="AcreditacionDocumentoPlataforma.Rechazar"/> crea instancias.
/// </summary>
public class RechazoAcreditacionDocumentoPlataforma : EntidadConTenant
{
    public const int LongitudMaximaMotivoLiteral = 1000;

    public Guid AcreditacionId { get; private set; }
    public CausaRechazoAcreditacion Causa { get; private set; }

    /// <summary>Texto literal que reporta la plataforma — distinto de <see cref="Causa"/>, que es la clasificación cerrada para poder agregar.</summary>
    public string MotivoLiteral { get; private set; } = string.Empty;

    public DateTime FechaUtc { get; private set; }

    private RechazoAcreditacionDocumentoPlataforma()
    {
    }

    internal RechazoAcreditacionDocumentoPlataforma(Guid acreditacionId, CausaRechazoAcreditacion causa, string motivoLiteral, DateTime fechaUtc)
    {
        if (acreditacionId == Guid.Empty)
            throw new ArgumentException("El rechazo debe pertenecer a una acreditación.", nameof(acreditacionId));
        if (string.IsNullOrWhiteSpace(motivoLiteral))
            throw new ArgumentException("El motivo literal de la plataforma es obligatorio.", nameof(motivoLiteral));

        var normalizado = motivoLiteral.Trim();
        if (normalizado.Length > LongitudMaximaMotivoLiteral)
            throw new ArgumentException($"El motivo no puede superar {LongitudMaximaMotivoLiteral} caracteres.", nameof(motivoLiteral));

        AcreditacionId = acreditacionId;
        Causa = causa;
        MotivoLiteral = normalizado;
        FechaUtc = fechaUtc;
    }
}
