using CaeManager.Application.Centros;
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
/// </summary>
public interface ISugerenciaVisitaCorreoService
{
    Task ProcesarAsync(Mensaje mensaje, Guid clienteId, CancellationToken cancellationToken = default);
}

public class SugerenciaVisitaCorreoService(
    ICentrosQueryContext centrosContext,
    IDeteccionVisitaCorreoService deteccion,
    ISugerenciaVisitaCorreoRepository sugerenciaRepositorio,
    ILogger<SugerenciaVisitaCorreoService> logger) : ISugerenciaVisitaCorreoService
{
    public async Task ProcesarAsync(Mensaje mensaje, Guid clienteId, CancellationToken cancellationToken = default)
    {
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
            deteccionDto.Resumen ?? "El correo parece solicitar una visita."));
    }
}
