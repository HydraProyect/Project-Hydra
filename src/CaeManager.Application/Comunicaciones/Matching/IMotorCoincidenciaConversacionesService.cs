using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Comunicaciones.Matching;

/// <summary>Desglose de puntos por criterio, tal cual la tabla de pesos de docs/COMUNICACIONES.md § 13.2 — expuesto siempre, aunque varios criterios estén en 0 hoy, para que la UI explique el porqué del score.</summary>
public record DesgloseCoincidenciaDto(
    int MismoCliente, int SimilaridadSemantica, int MismoTrabajador, int MismoCentro,
    int MismoDocumento, int MismoProyecto, int VentanaTemporal)
{
    public int Total => MismoCliente + SimilaridadSemantica + MismoTrabajador + MismoCentro + MismoDocumento + MismoProyecto + VentanaTemporal;
}

public record CandidatoCoincidenciaDto(Guid ConversacionId, string Asunto, DesgloseCoincidenciaDto Desglose);

/// <summary>
/// Conversation Matching Engine (docs/COMUNICACIONES.md § 13.2). Nunca
/// decide: solo propone. El gestor confirma siempre, vía
/// VincularConversacionCommand — mientras no confirme, el mensaje sigue
/// viviendo en su propia conversación (el enrutamiento de § 4 es el
/// fallback, sin cambios).
/// </summary>
public interface IMotorCoincidenciaConversacionesService
{
    /// <summary>
    /// Mejor candidata para vincular la conversación <paramref name="conversacionOrigenId"/>
    /// (del Cliente <paramref name="clienteId"/>, con último mensaje en
    /// <paramref name="fechaUltimoMensajeOrigenUtc"/>), o null si no hay
    /// ninguna otra conversación abierta de ese Cliente, o si la mejor no
    /// alcanza <see cref="MotorCoincidenciaConversacionesService.UmbralPropuesta"/>.
    /// Recibe escalares en vez de la entidad <see cref="Conversacion"/>
    /// completa a propósito: el único llamador hoy (ObtenerConversacionPorIdQueryHandler)
    /// es un handler de solo lectura que ya tiene estos datos proyectados —
    /// forzarlo a cargar el agregado completo (Mensajes+Participantes) solo
    /// para leer tres campos habría sido una ida y vuelta de más.
    /// </summary>
    Task<CandidatoCoincidenciaDto?> ProponerVinculacionAsync(
        Guid conversacionOrigenId, Guid clienteId, DateTime fechaUltimoMensajeOrigenUtc, CancellationToken cancellationToken = default);
}

/// <summary>
/// De los siete criterios de § 13.2, hoy solo dos tienen un dato real que
/// los respalde: Mismo Cliente (trivial — las candidatas ya vienen filtradas
/// por Cliente) y Ventana temporal (timestamps reales, sin inventar nada). El
/// resto se deja en 0 y documentado aquí mismo, en vez de fabricar una señal
/// que el sistema no tiene todavía (mismo criterio que ya se aplicó con
/// SugerenciaVisitaCorreo/DetectarCamposDocumentoQuery — CLAUDE.md):
///
/// - <b>Similaridad semántica</b>: no existe ningún servicio de
///   embeddings/similaridad vectorial en el repo (se buscó en
///   Application/DocumentosIa e Infrastructure/AsistenteIa). Construirlo es
///   una decisión de proveedor de IA nueva (modelo de embeddings,
///   almacenamiento vectorial) — fuera de alcance de esta pieza.
/// - <b>Mismo Trabajador / Mismo Centro</b>: un mensaje WhatsApp entrante no
///   trae Trabajador/Centro resuelto — <c>ParticipanteConversacion</c> no se
///   puebla en WhatsApp hoy (hueco ya identificado en § 14 del documento).
/// - <b>Mismo Documento</b>: no hay resolución adjunto→Documento antes de
///   que el gestor confirme nada en este flujo (la extracción real solo
///   corre dentro de "Actualizar documentación desde adjunto", ya con un
///   documento concreto elegido).
/// - <b>Mismo Proyecto</b>: <c>Proyecto</c> es un agregado real
///   (src/CaeManager.Domain/Proyectos/), pero es documentación de una
///   obra/instalación en un Centro — un concepto de negocio distinto, sin
///   ningún vínculo con Conversacion.
///
/// Cuando cualquiera de estos huecos se cierre (por ejemplo, si WhatsApp
/// empieza a poblar ParticipanteConversacion), solo cambia
/// <see cref="CalcularDesglose"/> — el resto del motor (candidatas, umbral,
/// DTO) no se toca.
/// </summary>
public class MotorCoincidenciaConversacionesService(IConversacionRepository repositorio) : IMotorCoincidenciaConversacionesService
{
    public const int PuntosMismoCliente = 40;
    public const int PuntosSimilaridadSemantica = 35;
    public const int PuntosMismoTrabajador = 25;
    public const int PuntosMismoCentro = 20;
    public const int PuntosMismoDocumento = 20;
    public const int PuntosMismoProyecto = 15;
    public const int PuntosVentanaTemporal = 10;

    public static readonly TimeSpan VentanaTemporal = TimeSpan.FromHours(72);

    /// <summary>
    /// Con las señales reales disponibles hoy, el máximo alcanzable es 50
    /// (Cliente + Ventana) — se propone solo cuando ambas coinciden. Cuando
    /// se activen Trabajador/Centro/Documento el umbral seguirá siendo válido
    /// (un candidato con más señales reales solo puede sumar más, nunca menos).
    /// </summary>
    public const int UmbralPropuesta = PuntosMismoCliente + PuntosVentanaTemporal;

    public async Task<CandidatoCoincidenciaDto?> ProponerVinculacionAsync(
        Guid conversacionOrigenId, Guid clienteId, DateTime fechaUltimoMensajeOrigenUtc, CancellationToken cancellationToken = default)
    {
        var candidatas = await repositorio.ObtenerAbiertasPorClienteAsync(clienteId, conversacionOrigenId, cancellationToken);
        if (candidatas.Count == 0) return null;

        var mejor = candidatas
            .Select(c => new CandidatoCoincidenciaDto(c.Id, c.Asunto, CalcularDesglose(fechaUltimoMensajeOrigenUtc, c.FechaUltimoMensajeUtc)))
            .OrderByDescending(c => c.Desglose.Total)
            .First();

        return mejor.Desglose.Total >= UmbralPropuesta ? mejor : null;
    }

    private static DesgloseCoincidenciaDto CalcularDesglose(DateTime fechaUltimoMensajeOrigenUtc, DateTime fechaUltimoMensajeCandidataUtc)
    {
        var diferencia = fechaUltimoMensajeOrigenUtc >= fechaUltimoMensajeCandidataUtc
            ? fechaUltimoMensajeOrigenUtc - fechaUltimoMensajeCandidataUtc
            : fechaUltimoMensajeCandidataUtc - fechaUltimoMensajeOrigenUtc;

        return new DesgloseCoincidenciaDto(
            MismoCliente: PuntosMismoCliente,
            SimilaridadSemantica: 0,
            MismoTrabajador: 0,
            MismoCentro: 0,
            MismoDocumento: 0,
            MismoProyecto: 0,
            VentanaTemporal: diferencia < VentanaTemporal ? PuntosVentanaTemporal : 0);
    }
}
