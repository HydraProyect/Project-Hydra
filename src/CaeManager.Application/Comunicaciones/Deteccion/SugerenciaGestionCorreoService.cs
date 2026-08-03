using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.Deteccion;

/// <summary>
/// Orquestador puro (mismo patrón que SugerenciaVisitaCorreoService): llamado
/// desde IngestaWebhookService justo después de persistir un MensajeCorreo
/// Entrante, carga los Trabajadores con asignación activa en algún Centro
/// del Cliente de la conversación y los TipoDocumento de Ámbito Trabajador,
/// pide a <see cref="IDeteccionGestionCorreoService"/> que clasifique el
/// correo, y si parece pedir la actualización de un documento registra una
/// <see cref="SugerenciaGestionCorreo"/> pendiente. No guarda cambios — el
/// llamador ya persiste todo el mensaje ingerido en una sola operación.
/// </summary>
public interface ISugerenciaGestionCorreoService
{
    Task ProcesarAsync(MensajeCorreo mensaje, Guid clienteId, CancellationToken cancellationToken = default);
}

public class SugerenciaGestionCorreoService(
    ICentrosQueryContext centrosContext,
    IAsignacionesQueryContext asignacionesContext,
    ITrabajadoresQueryContext trabajadoresContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IDeteccionGestionCorreoService deteccion,
    ISugerenciaGestionCorreoRepository sugerenciaRepositorio,
    ILogger<SugerenciaGestionCorreoService> logger) : ISugerenciaGestionCorreoService
{
    public async Task ProcesarAsync(MensajeCorreo mensaje, Guid clienteId, CancellationToken cancellationToken = default)
    {
        var centroIds = await centrosContext.Centros
            .Where(c => c.ClienteId == clienteId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (centroIds.Count == 0)
            return;

        var trabajadorIds = await asignacionesContext.Asignaciones
            .Where(a => centroIds.Contains(a.CentroId) && a.FechaBaja == null)
            .Select(a => a.TrabajadorId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Sin Trabajadores con alta activa en algún Centro del Cliente no hay
        // a quién asociar la sugerencia — v1 no cubre "sugerir un Trabajador
        // nuevo" (YAGNI, mismo criterio que SugerenciaVisitaCorreoService).
        if (trabajadorIds.Count == 0)
            return;

        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .Select(t => new TrabajadorCandidatoGestionDto(t.Id, t.Nombre + " " + t.Apellidos, t.Dni))
            .ToListAsync(cancellationToken);

        var tiposDocumento = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new TipoDocumentoCandidatoGestionDto(t.Id, t.Nombre))
            .ToListAsync(cancellationToken);

        if (tiposDocumento.Count == 0)
            return;

        var resultado = await deteccion.DetectarAsync(mensaje.CuerpoHtml, trabajadores, tiposDocumento, cancellationToken);

        if (resultado.EsFallido)
        {
            logger.LogInformation(
                "Detección de gestión por correo no disponible para el mensaje {MensajeId}: {Codigo}", mensaje.Id, resultado.Error.Codigo);
            return;
        }

        var deteccionDto = resultado.Valor;
        if (!deteccionDto.EsActualizacionDocumento)
            return;

        sugerenciaRepositorio.Agregar(new SugerenciaGestionCorreo(
            mensaje.Id, deteccionDto.TrabajadorId, deteccionDto.TipoDocumentoId,
            deteccionDto.Resumen ?? "El correo parece pedir la actualización de un documento."));
    }
}
