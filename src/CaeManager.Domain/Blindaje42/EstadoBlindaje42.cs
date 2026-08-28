namespace CaeManager.Domain.Blindaje42;

/// <summary>
/// Estado derivado de una <see cref="SolicitudCertificacionTgss"/> concreta
/// frente a la responsabilidad solidaria del art. 42.2 ET — nunca se
/// almacena, lo calcula <see cref="CalculadoraEstadoBlindaje42"/>. Es el
/// estado de ESA solicitud, no del Cliente frente a la Empresa en general:
/// solo cubre los descubiertos existentes en la fecha de la solicitud
/// (STS 124/2021, 3-feb-2021) — una contrata larga necesita solicitudes
/// periódicas para mantener el blindaje sobre los descubiertos que el
/// contratista genere después.
/// </summary>
public enum EstadoBlindaje42
{
    /// <summary>Solicitada, sin respuesta de la TGSS y todavía dentro del plazo de 30 días.</summary>
    PendienteRespuesta,

    /// <summary>Sin respuesta transcurrido el plazo — exoneración automática por el propio art. 42.1 ET.</summary>
    ExoneradaPorSilencio,

    /// <summary>La TGSS certificó que la Empresa no tenía descubiertos en la fecha de la solicitud.</summary>
    ExoneradaPorCertificacion,

    /// <summary>La TGSS certificó que la Empresa sí tenía descubiertos — no hay exoneración.</summary>
    NoExonerada
}
