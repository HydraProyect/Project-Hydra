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
/// desde IngestaWebhookService justo después de persistir un Mensaje
/// Entrante, carga los Trabajadores con asignación activa en algún Centro
/// del Cliente de la conversación y los TipoDocumento de Ámbito Trabajador,
/// pide a <see cref="IDeteccionGestionCorreoService"/> que clasifique el
/// correo, y si parece pedir la actualización de uno o varios documentos
/// registra una <see cref="SugerenciaGestionCorreo"/> pendiente con un
/// <see cref="DetalleSugerenciaGestionCorreo"/> por ítem detectado. No
/// guarda cambios — el llamador ya persiste todo el mensaje ingerido en una
/// sola operación. Devuelve la sugerencia creada (o null si no aplica) para
/// que otros pasos de la ingesta (ronda de reducción de ruido en
/// Comunicaciones) puedan reutilizar la misma extracción sin pagar una
/// segunda llamada a IA.
/// </summary>
public interface ISugerenciaGestionCorreoService
{
    Task<SugerenciaGestionCorreo?> ProcesarAsync(Mensaje mensaje, Guid clienteId, CancellationToken cancellationToken = default);
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
    public async Task<SugerenciaGestionCorreo?> ProcesarAsync(Mensaje mensaje, Guid clienteId, CancellationToken cancellationToken = default)
    {
        var centroIds = await centrosContext.Centros
            .Where(c => c.ClienteId == clienteId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (centroIds.Count == 0)
            return null;

        var trabajadorIds = await asignacionesContext.Asignaciones
            .Where(a => centroIds.Contains(a.CentroId) && a.FechaBaja == null)
            .Select(a => a.TrabajadorId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Sin Trabajadores con alta activa en algún Centro del Cliente no hay
        // a quién asociar la sugerencia — v1 no cubre "sugerir un Trabajador
        // nuevo" (YAGNI, mismo criterio que SugerenciaVisitaCorreoService).
        if (trabajadorIds.Count == 0)
            return null;

        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .Select(t => new TrabajadorCandidatoGestionDto(t.Id, t.Nombre + " " + t.Apellidos, t.Dni))
            .ToListAsync(cancellationToken);

        var tiposDocumento = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new TipoDocumentoCandidatoGestionDto(t.Id, t.Nombre))
            .ToListAsync(cancellationToken);

        if (tiposDocumento.Count == 0)
            return null;

        var resultado = await deteccion.DetectarAsync(mensaje.CuerpoHtml, trabajadores, tiposDocumento, cancellationToken);

        if (resultado.EsFallido)
        {
            logger.LogInformation(
                "Detección de gestión por correo no disponible para el mensaje {MensajeId}: {Codigo}", mensaje.Id, resultado.Error.Codigo);
            return null;
        }

        var deteccionDto = resultado.Valor;
        if (!deteccionDto.EsActualizacionDocumento || deteccionDto.Items.Count == 0)
            return null;

        var sugerencia = new SugerenciaGestionCorreo(
            mensaje.Id, deteccionDto.Resumen ?? "El correo parece pedir la actualización de un documento.", deteccionDto.Confianza);

        foreach (var item in deteccionDto.Items)
            sugerencia.AgregarDetalle(item.TrabajadorId, item.TipoDocumentoId, item.ConfianzaTrabajador, item.ConfianzaTipoDocumento);

        sugerenciaRepositorio.Agregar(sugerencia);
        return sugerencia;
    }
}
