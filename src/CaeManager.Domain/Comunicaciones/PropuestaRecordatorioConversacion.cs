using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

public enum EstadoPropuestaRecordatorio
{
    Pendiente = 0,
    Aprobada = 1,
    Descartada = 2
}

/// <summary>
/// Recordatorio que TALVEG <b>propone</b> cuando una <see cref="Conversacion"/> lleva
/// <see cref="DiasLaborablesDeEspera"/> días laborables en <see cref="EstadoConversacion.EsperandoRespuestaExterna"/> sin
/// respuesta (decisión de producto aprobada el 2026-10-17). <b>Nada se envía solo</b>: el Gestor CAE lo aprueba (y entonces
/// sale por el camino normal de respuesta del hilo) o lo descarta.
///
/// <para>
/// Una propuesta pertenece a <b>una espera concreta</b>, identificada por el <see cref="Conversacion.EstadoDesdeUtc"/> con
/// que entró en ella (<see cref="EsperaDesdeUtc"/>): la misma espera nunca recibe una segunda propuesta, la apruebe o la
/// descarte el Gestor CAE. Si la conversación sale de esa espera —llega la respuesta externa y se reabre, o alguien cambia
/// el estado—, la propuesta deja de estar vigente (<see cref="SigueVigentePara"/>) y el proceso de fondo la descarta sola.
/// </para>
///
/// <para>
/// Vive en el Tenant propietario de la conversación. Sin navegación desde <see cref="Conversacion"/> (mismo criterio que
/// <see cref="NotaInternaConversacion"/>); la integridad con el hilo la sostiene una FK compuesta (ConversacionId, TenantId)
/// creada en SQL por la migración. Sin columna de persona: quién aprobó o descartó queda en la auditoría general.
/// </para>
/// </summary>
public class PropuestaRecordatorioConversacion : EntidadBase
{
    /// <summary>Días laborables del calendario del Operador CAE que una espera aguanta antes de proponer el recordatorio.</summary>
    public const int DiasLaborablesDeEspera = 2;

    public Guid ConversacionId { get; private set; }

    /// <summary>Instante en que la conversación entró en la espera a la que responde esta propuesta.</summary>
    public DateTime EsperaDesdeUtc { get; private set; }

    public DateTime PropuestaEnUtc { get; private set; }
    public EstadoPropuestaRecordatorio Estado { get; private set; }
    public DateTime? ResueltaEnUtc { get; private set; }

    private PropuestaRecordatorioConversacion()
    {
    }

    private PropuestaRecordatorioConversacion(Guid conversacionId, DateTime esperaDesdeUtc, DateTime propuestaEnUtc)
    {
        ConversacionId = conversacionId;
        EsperaDesdeUtc = esperaDesdeUtc;
        PropuestaEnUtc = propuestaEnUtc;
        Estado = EstadoPropuestaRecordatorio.Pendiente;
    }

    /// <summary>
    /// Instante a partir del cual corresponde proponer el recordatorio de una espera que empezó en
    /// <paramref name="esperaDesdeUtc"/>, según el calendario laboral del Operador CAE.
    /// </summary>
    public static DateTime VenceEn(DateTime esperaDesdeUtc, CalendarioLaboralOperadorCae calendario) =>
        calendario.SumarDiasLaborables(
            new DateTimeOffset(DateTime.SpecifyKind(esperaDesdeUtc, DateTimeKind.Utc)), DiasLaborablesDeEspera).UtcDateTime;

    /// <summary>
    /// Propone el recordatorio si la conversación es de correo, sigue esperando la respuesta externa y ya han pasado los
    /// días laborables de espera. Devuelve <c>null</c> si no corresponde. No comprueba si esa espera ya tuvo propuesta: eso
    /// lo garantiza quien la guarda (y el índice único de la tabla).
    /// <para>
    /// Solo correo: WhatsApp solo admite texto libre en las 24 h siguientes al último entrante, y una espera de dos días
    /// laborables sin respuesta deja esa ventana siempre cerrada, así que el recordatorio no podría enviarse.
    /// </para>
    /// </summary>
    public static PropuestaRecordatorioConversacion? ProponerSiCorresponde(
        Conversacion conversacion, DateTime ahoraUtc, CalendarioLaboralOperadorCae calendario)
    {
        ArgumentNullException.ThrowIfNull(conversacion);
        ArgumentNullException.ThrowIfNull(calendario);

        if (conversacion.Canal != CanalConversacion.Correo
            || conversacion.Estado != EstadoConversacion.EsperandoRespuestaExterna
            || ahoraUtc < VenceEn(conversacion.EstadoDesdeUtc, calendario))
            return null;

        return new PropuestaRecordatorioConversacion(conversacion.Id, conversacion.EstadoDesdeUtc, ahoraUtc);
    }

    /// <summary>La conversación sigue en la misma espera para la que se hizo la propuesta.</summary>
    public bool SigueVigentePara(EstadoConversacion estado, DateTime estadoDesdeUtc) =>
        estado == EstadoConversacion.EsperandoRespuestaExterna && estadoDesdeUtc == EsperaDesdeUtc;

    public bool SigueVigentePara(Conversacion conversacion) =>
        conversacion.Id == ConversacionId && SigueVigentePara(conversacion.Estado, conversacion.EstadoDesdeUtc);

    /// <summary>
    /// El Gestor CAE aprueba el envío. Se marca antes de enviar, y se guarda, para que dos aprobaciones simultáneas no
    /// envíen dos correos: la segunda choca con el token de concurrencia. Si el envío falla, <see cref="LiberarTrasEnvioFallido"/>.
    /// </summary>
    public Result Aprobar(DateTime ahoraUtc)
    {
        if (Estado != EstadoPropuestaRecordatorio.Pendiente)
            return Result.Fallo(ErrorYaResuelta);

        Estado = EstadoPropuestaRecordatorio.Aprobada;
        ResueltaEnUtc = ahoraUtc;
        return Result.Exito();
    }

    /// <summary>Devuelve a Pendiente una aprobación cuyo envío no llegó a salir.</summary>
    public void LiberarTrasEnvioFallido()
    {
        if (Estado != EstadoPropuestaRecordatorio.Aprobada) return;
        Estado = EstadoPropuestaRecordatorio.Pendiente;
        ResueltaEnUtc = null;
    }

    /// <summary>Lo descarta el Gestor CAE, o el Sistema cuando la espera terminó. Solo una propuesta Pendiente.</summary>
    public Result Descartar(DateTime ahoraUtc)
    {
        if (Estado != EstadoPropuestaRecordatorio.Pendiente)
            return Result.Fallo(ErrorYaResuelta);

        Estado = EstadoPropuestaRecordatorio.Descartada;
        ResueltaEnUtc = ahoraUtc;
        return Result.Exito();
    }

    public static readonly Error ErrorYaResuelta =
        Error.Crear("Recordatorio.YaResuelto", "Este recordatorio ya se aprobó o se descartó.");
}
