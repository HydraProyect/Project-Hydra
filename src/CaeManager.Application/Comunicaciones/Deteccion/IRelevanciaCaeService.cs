using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Domain.Comunicaciones;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.Deteccion;

/// <summary>
/// Orquestador puro (mismo patrón que SugerenciaVisitaCorreoService): llamado desde
/// IngestaWebhookService justo después de persistir un Mensaje Entrante en una conversación con
/// Cliente resuelto. Detecta el patrón "conversación pre-CAE" (ronda de reducción de ruido en
/// Comunicaciones): el cliente copia al gestor desde el inicio de una negociación comercial con un
/// Centro/Empresa, antes de que exista ninguna gestión CAE real. Se re-evalúa en cada mensaje
/// entrante nuevo mientras la conversación siga clasificada como no accionable — en cuanto un
/// mensaje hace que la IA la marque accionable, la clasificación se congela ahí (ver
/// <see cref="DebeReevaluar"/>): nunca vuelve a tratarse como informativa aunque un mensaje
/// posterior "suene" otra vez a negociación pura. No guarda cambios — el llamador ya persiste todo
/// el mensaje ingerido en una sola operación.
///
/// Consumidor de IA con gate de Nivel 0 (DEC-33, REC-035): sin instrucción de tratamiento vigente
/// del Tenant propietario no se envía nada al proveedor y la conversación se queda sin clasificar.
/// </summary>
public interface IRelevanciaCaeService
{
    Task ProcesarAsync(Conversacion conversacion, CancellationToken cancellationToken = default);
}

public class RelevanciaCaeService(
    IDeteccionRelevanciaCaeService deteccion,
    IClasificacionRelevanciaCaeRepository clasificacionRepositorio,
    IInstruccionTratamientoIaService instruccionTratamientoIa,
    ITenantActual tenantActual,
    ILogger<RelevanciaCaeService> logger) : IRelevanciaCaeService
{
    // Tope defensivo, mismo criterio que el resto de servicios de detección de este módulo — un
    // hilo con mucho histórico no necesita enviarse entero para clasificar relevancia CAE. Se
    // conserva la COLA (mensajes más recientes) en vez del principio: lo que decide si la
    // conversación ya se volvió accionable es lo último que se dijo, no cómo empezó.
    private const int LongitudMaximaCuerpo = 12000;

    public async Task ProcesarAsync(Conversacion conversacion, CancellationToken cancellationToken = default)
    {
        // Nivel 0 (DEC-33, REC-035) — ver el mismo gate en VerificacionIaDocumentoService.
        // Aquí lo que viaja al proveedor es la transcripción completa del hilo de
        // correo del Tenant propietario, así que el gate va por delante de todo,
        // incluida la lectura de la clasificación previa. A diferencia de los
        // consumidores de Documentos, este corre en un proceso de fondo
        // (IngestaWebhookHostedService) donde nadie ve una pantalla vacía: sin log,
        // el síntoma sería "las conversaciones no se clasifican" descubierto
        // semanas después y sin rastro del motivo.
        if (tenantActual.TenantId is not { } tenantId || !await instruccionTratamientoIa.EstaHabilitadaAsync(tenantId, cancellationToken))
        {
            logger.LogInformation(
                "Detección de relevancia CAE omitida para la conversación {ConversacionId}: el Tenant propietario no tiene instrucción de tratamiento IA vigente (Nivel 0).",
                conversacion.Id);
            return;
        }

        var existente = await clasificacionRepositorio.ObtenerPorConversacionIdAsync(conversacion.Id, cancellationToken);
        if (!DebeReevaluar(existente))
            return;

        var resultado = await deteccion.DetectarAsync(ConstruirTranscripcion(conversacion), cancellationToken);

        if (resultado.EsFallido)
        {
            logger.LogInformation(
                "Detección de relevancia CAE no disponible para la conversación {ConversacionId}: {Codigo}",
                conversacion.Id, resultado.Error.Codigo);
            return;
        }

        var deteccionDto = resultado.Valor;
        var resumen = deteccionDto.Resumen ?? "Sin gestión CAE detectada todavía.";

        if (existente is null)
            clasificacionRepositorio.Agregar(new ClasificacionRelevanciaCae(conversacion.Id, deteccionDto.EsAccionableCae, resumen, deteccionDto.Confianza));
        else
            existente.Actualizar(deteccionDto.EsAccionableCae, resumen, deteccionDto.Confianza);
    }

    /// <summary>
    /// Sin clasificación previa, siempre hay que evaluar. Con clasificación previa, solo se
    /// re-evalúa mientras siga siendo no accionable — una vez accionable, queda congelada
    /// (no hay vuelta atrás a "informativa").
    /// </summary>
    public static bool DebeReevaluar(ClasificacionRelevanciaCae? clasificacionExistente) =>
        clasificacionExistente is null || !clasificacionExistente.EsAccionableCae;

    private static string ConstruirTranscripcion(Conversacion conversacion)
    {
        var builder = new StringBuilder();
        foreach (var mensaje in conversacion.Mensajes.OrderBy(m => m.FechaUtc))
        {
            builder.AppendLine($"--- {mensaje.Direccion} ({mensaje.Remitente}, {mensaje.FechaUtc:yyyy-MM-dd}) ---");
            builder.AppendLine(mensaje.CuerpoHtml);
            builder.AppendLine();
        }

        var texto = builder.ToString();
        return texto.Length > LongitudMaximaCuerpo ? texto[^LongitudMaximaCuerpo..] : texto;
    }
}
