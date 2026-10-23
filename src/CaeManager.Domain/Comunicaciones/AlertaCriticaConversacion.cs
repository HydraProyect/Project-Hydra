using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

public enum EstadoAlertaCritica { Pendiente, ConfirmadaYDerivada, Descartada }

/// <summary>La única identidad registrada corresponde a la decisión humana de Dirección CAE o Coordinador CAE.</summary>
public sealed class AlertaCriticaConversacion : EntidadConTenant, IVersionable
{
    public Guid ConversacionId { get; private set; }
    public Guid MensajeId { get; private set; }
    public string TextoDisparador { get; private set; } = string.Empty;
    public string ExpresionDetectada { get; private set; } = string.Empty;
    public Guid OperadorCaeTenantId { get; private set; }
    public bool? SegundaOpinionIa { get; private set; }
    public int? ConfianzaIa { get; private set; }
    public EstadoAlertaCritica Estado { get; private set; }
    public DateTime DetectadaEnUtc { get; private set; }
    public Guid? DecisionPorUsuarioId { get; private set; }
    public DateTime? DecisionEnUtc { get; private set; }
    public Guid Version { get; private set; } = Guid.NewGuid();

    private AlertaCriticaConversacion() { }

    public AlertaCriticaConversacion(Mensaje mensaje, string textoDisparador, string expresionDetectada,
        Guid operadorCaeTenantId, DateTime ahora)
    {
        ArgumentNullException.ThrowIfNull(mensaje);
        if (mensaje.Direccion != DireccionMensaje.Entrante)
            throw new ArgumentException("Solo un mensaje entrante puede disparar una alerta crítica.", nameof(mensaje));
        if (string.IsNullOrWhiteSpace(textoDisparador) || textoDisparador.Length > 4000)
            throw new ArgumentException("El texto disparador debe contener entre 1 y 4000 caracteres.", nameof(textoDisparador));
        if (string.IsNullOrWhiteSpace(expresionDetectada) || expresionDetectada.Length > 200)
            throw new ArgumentException("La expresión detectada debe contener entre 1 y 200 caracteres.", nameof(expresionDetectada));
        if (operadorCaeTenantId == Guid.Empty)
            throw new ArgumentException("El Operador CAE debe tener un Tenant de origen válido.", nameof(operadorCaeTenantId));
        ConversacionId = mensaje.ConversacionId;
        MensajeId = mensaje.Id;
        TextoDisparador = textoDisparador;
        ExpresionDetectada = expresionDetectada;
        OperadorCaeTenantId = operadorCaeTenantId;
        DetectadaEnUtc = ahora;
    }

    public void AnotarSegundaOpinion(bool esCritica, int confianza)
    {
        if (confianza is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(confianza));
        SegundaOpinionIa = esCritica;
        ConfianzaIa = confianza;
    }

    /// <summary>Incidencia registrada al confirmar y derivar; ata el Deshacer a esa Incidencia concreta. Null si se descartó o se confirmó sin Incidencia.</summary>
    public Guid? IncidenciaId { get; private set; }

    public void Resolver(bool confirmarYDerivar, Guid decisionPorUsuarioId, DateTime ahora, Guid? incidenciaId = null)
    {
        if (Estado != EstadoAlertaCritica.Pendiente) throw new InvalidOperationException("La alerta ya tiene una decisión.");
        if (decisionPorUsuarioId == Guid.Empty) throw new ArgumentException("La decisión requiere una persona identificada.", nameof(decisionPorUsuarioId));
        if (incidenciaId is { } id && (!confirmarYDerivar || id == Guid.Empty))
            throw new ArgumentException("Solo una alerta confirmada y derivada se ata a una Incidencia.", nameof(incidenciaId));
        Estado = confirmarYDerivar ? EstadoAlertaCritica.ConfirmadaYDerivada : EstadoAlertaCritica.Descartada;
        DecisionPorUsuarioId = decisionPorUsuarioId;
        DecisionEnUtc = ahora;
        IncidenciaId = incidenciaId;
    }

    /// <summary>
    /// «Deshacer» de quien confirmó y derivó: la alerta vuelve a Pendiente y suelta la Incidencia, que el llamador elimina en la
    /// misma unidad de trabajo. Solo la persona que tomó la decisión y solo sobre la Incidencia que registró.
    /// </summary>
    public void DeshacerDerivacion(Guid incidenciaId, Guid usuarioId)
    {
        if (Estado != EstadoAlertaCritica.ConfirmadaYDerivada || IncidenciaId is null || IncidenciaId != incidenciaId)
            throw new InvalidOperationException("Solo se deshace la derivación de la Incidencia que se registró con ella.");
        if (usuarioId == Guid.Empty || DecisionPorUsuarioId != usuarioId)
            throw new InvalidOperationException("Solo quien confirmó y derivó la alerta puede deshacerlo.");
        Estado = EstadoAlertaCritica.Pendiente;
        DecisionPorUsuarioId = null;
        DecisionEnUtc = null;
        IncidenciaId = null;
    }
}
