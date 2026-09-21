using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.Deteccion;

/// <summary>
/// Orquestador puro (igual que <c>DeteccionTrabajadoresService</c>): llamado
/// desde <c>IngestaWebhookService</c> justo después de persistir un
/// Mensaje Entrante, carga los Centros del Cliente de la conversación,
/// pide a <see cref="IDeteccionVisitaCorreoService"/> que lo clasifique, y si
/// parece una solicitud de visita registra una <see cref="SugerenciaVisitaCorreo"/>
/// pendiente. No guarda cambios — el llamador ya persiste todo el mensaje
/// ingerido en una sola operación (mismo patrón que el resto de
/// IngestaWebhookService).
///
/// Consumidor de IA con gate de Nivel 0 (DEC-33, REC-035): sin instrucción de
/// tratamiento vigente del Tenant propietario no se envía el cuerpo del correo
/// al proveedor y no se propone ninguna visita.
/// </summary>
public interface ISugerenciaVisitaCorreoService
{
    Task ProcesarAsync(Mensaje mensaje, Guid clienteId, CancellationToken cancellationToken = default);
}

public class SugerenciaVisitaCorreoService(
    ICentrosQueryContext centrosContext,
    IDeteccionVisitaCorreoService deteccion,
    ISugerenciaVisitaCorreoRepository sugerenciaRepositorio,
    IInstruccionTratamientoIaService instruccionTratamientoIa,
    ITenantActual tenantActual,
    ILogger<SugerenciaVisitaCorreoService> logger) : ISugerenciaVisitaCorreoService
{
    public async Task ProcesarAsync(Mensaje mensaje, Guid clienteId, CancellationToken cancellationToken = default)
    {
        // Nivel 0 (DEC-33, REC-035) — ver el mismo gate en VerificacionIaDocumentoService.
        // El cuerpo del correo puede traer nombres y datos de contacto de personas,
        // así que el gate va por delante incluso de cargar los Centros candidatos.
        // Se registra el motivo porque esto corre en un proceso de fondo, sin
        // pantalla que muestre "sin sugerencias".
        if (tenantActual.TenantId is not { } tenantId || !await instruccionTratamientoIa.EstaHabilitadaAsync(tenantId, cancellationToken))
        {
            logger.LogInformation(
                "Detección de visita por correo omitida para el mensaje {MensajeId}: el Tenant propietario no tiene instrucción de tratamiento IA vigente (Nivel 0).",
                mensaje.Id);
            return;
        }

        var centros = await centrosContext.Centros
            .Where(c => c.ClienteId == clienteId)
            .Select(c => new CentroCandidatoVisitaDto(c.Id, c.Nombre))
            .ToListAsync(cancellationToken);

        // Sin Centros del Cliente no hay nada a lo que asociar la sugerencia
        // — v1 no cubre "sugerir crear el Centro primero" (YAGNI).
        if (centros.Count == 0)
            return;

        var resultado = await deteccion.DetectarAsync(
            mensaje.CuerpoHtml, centros, DateOnly.FromDateTime(mensaje.FechaUtc), cancellationToken);

        if (resultado.EsFallido)
        {
            logger.LogInformation(
                "Detección de visita por correo no disponible para el mensaje {MensajeId}: {Codigo}", mensaje.Id, resultado.Error.Codigo);
            return;
        }

        var deteccionDto = resultado.Valor;
        if (!deteccionDto.EsSolicitudVisita)
            return;

        sugerenciaRepositorio.Agregar(new SugerenciaVisitaCorreo(
            mensaje.Id, deteccionDto.CentroId, deteccionDto.FechaInicio, deteccionDto.FechaFin,
            deteccionDto.Resumen ?? "El correo parece solicitar una visita.",
            deteccionDto.Confianza, deteccionDto.ConfianzaCentro, deteccionDto.ConfianzaFechas));
    }
}
