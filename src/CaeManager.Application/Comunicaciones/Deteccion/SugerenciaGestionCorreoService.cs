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
/// Resultado de <see cref="ISugerenciaGestionCorreoService.ProcesarAsync"/> — exactamente uno de
/// los dos es no-null, nunca ambos: <paramref name="Sugerencia"/> cuando la IA identificó ítems
/// concretos, <paramref name="ResumenAgregado"/> cuando el correo es de una plataforma de solo
/// resumen sin desglose por persona (ronda de reducción de ruido en Comunicaciones). Ambos null
/// cuando el correo no aplica.
/// </summary>
public record ResultadoDeteccionGestionDto(SugerenciaGestionCorreo? Sugerencia, ResumenAgregadoGestionDto? ResumenAgregado)
{
    public static readonly ResultadoDeteccionGestionDto Vacio = new(null, null);
}

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
/// sola operación. Devuelve el resultado de la extracción (o vacío si no
/// aplica) para que otros pasos de la ingesta (ronda de reducción de ruido
/// en Comunicaciones) puedan reutilizar la misma llamada a IA sin pagar una
/// segunda.
/// </summary>
public interface ISugerenciaGestionCorreoService
{
    Task<ResultadoDeteccionGestionDto> ProcesarAsync(Mensaje mensaje, Guid clienteId, CancellationToken cancellationToken = default);
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
    public async Task<ResultadoDeteccionGestionDto> ProcesarAsync(Mensaje mensaje, Guid clienteId, CancellationToken cancellationToken = default)
    {
        var centroIds = await centrosContext.Centros
            .Where(c => c.ClienteId == clienteId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (centroIds.Count == 0)
            return ResultadoDeteccionGestionDto.Vacio;

        var trabajadorIds = await asignacionesContext.Asignaciones
            .Where(a => centroIds.Contains(a.CentroId) && a.FechaBaja == null)
            .Select(a => a.TrabajadorId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Sin Trabajadores con alta activa en algún Centro del Cliente no hay
        // a quién asociar la sugerencia — v1 no cubre "sugerir un Trabajador
        // nuevo" (YAGNI, mismo criterio que SugerenciaVisitaCorreoService).
        if (trabajadorIds.Count == 0)
            return ResultadoDeteccionGestionDto.Vacio;

        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .Select(t => new TrabajadorCandidatoGestionDto(t.Id, t.Nombre + " " + t.Apellidos, t.Dni))
            .ToListAsync(cancellationToken);

        var tiposDocumento = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new TipoDocumentoCandidatoGestionDto(t.Id, t.Nombre))
            .ToListAsync(cancellationToken);

        if (tiposDocumento.Count == 0)
            return ResultadoDeteccionGestionDto.Vacio;

        var resultado = await deteccion.DetectarAsync(mensaje.CuerpoHtml, trabajadores, tiposDocumento, cancellationToken);

        if (resultado.EsFallido)
        {
            logger.LogInformation(
                "Detección de gestión por correo no disponible para el mensaje {MensajeId}: {Codigo}", mensaje.Id, resultado.Error.Codigo);
            return ResultadoDeteccionGestionDto.Vacio;
        }

        var deteccionDto = resultado.Valor;
        if (!deteccionDto.EsActualizacionDocumento)
            return ResultadoDeteccionGestionDto.Vacio;

        if (deteccionDto.Items.Count == 0)
            return new ResultadoDeteccionGestionDto(null, deteccionDto.ResumenAgregado);

        var sugerencia = new SugerenciaGestionCorreo(
            mensaje.Id, deteccionDto.Resumen ?? "El correo parece pedir la actualización de un documento.", deteccionDto.Confianza);

        foreach (var item in deteccionDto.Items)
            sugerencia.AgregarDetalle(item.TrabajadorId, item.TipoDocumentoId, item.ConfianzaTrabajador, item.ConfianzaTipoDocumento);

        sugerenciaRepositorio.Agregar(sugerencia);
        return new ResultadoDeteccionGestionDto(sugerencia, null);
    }
}
