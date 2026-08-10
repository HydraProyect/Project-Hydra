using CaeManager.Application.Documentos;
using CaeManager.Application.Reclamaciones;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Deteccion;

/// <summary>
/// Marca los ítems de una <see cref="SugerenciaGestionCorreo"/> que repiten un pendiente ya
/// reclamado formalmente al Cliente (ronda de reducción de ruido en Comunicaciones). Llamado desde
/// IngestaWebhookService justo después de <see cref="ISugerenciaGestionCorreoService.ProcesarAsync"/>,
/// reutilizando la misma sugerencia ya extraída — sin segunda llamada a IA. No guarda cambios — el
/// llamador ya persiste todo el mensaje ingerido en una sola operación.
/// </summary>
public interface IClasificacionRuidoMensajeService
{
    Task ProcesarAsync(
        SugerenciaGestionCorreo sugerencia, Guid clienteId, bool esNotificacionAutomatica, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compara un resumen agregado de plataforma ("3 pendientes, 1 vencido, 0 rechazados") contra
    /// el último conocido para el mismo (Cliente, Plataforma) y actualiza el registro. Devuelve
    /// true si los tres contadores son idénticos a la última vez — el mensaje no aporta ninguna
    /// novedad y debe demotarse (<see cref="MotivoRuidoMensaje.ResumenSinCambios"/>); false si algo
    /// cambió (o es la primera vez que se ve esta plataforma para este Cliente), en cuyo caso el
    /// mensaje se deja visible aunque no se sepa el qué exactamente cambió.
    /// </summary>
    Task<bool> ProcesarResumenAsync(
        Guid clienteId, Guid proveedorPlataformaCaeId, ResumenAgregadoGestionDto resumen, CancellationToken cancellationToken = default);
}

public class ClasificacionRuidoMensajeService(
    IReclamacionesQueryContext reclamacionesContext,
    IDocumentosQueryContext documentosContext,
    IClasificacionRuidoDetalleGestionRepository repositorio,
    IUltimoResumenNotificacionPlataformaRepository resumenRepositorio) : IClasificacionRuidoMensajeService
{
    public async Task<bool> ProcesarResumenAsync(
        Guid clienteId, Guid proveedorPlataformaCaeId, ResumenAgregadoGestionDto resumen, CancellationToken cancellationToken = default)
    {
        var ultimo = await resumenRepositorio.ObtenerAsync(clienteId, proveedorPlataformaCaeId, cancellationToken);
        var ahora = DateTime.UtcNow;

        if (ultimo is null)
        {
            resumenRepositorio.Agregar(new UltimoResumenNotificacionPlataforma(
                clienteId, proveedorPlataformaCaeId, resumen.Pendientes, resumen.Vencidos, resumen.Rechazados, ahora));
            return false;
        }

        var sinCambios = ultimo.CoincideCon(resumen.Pendientes, resumen.Vencidos, resumen.Rechazados);
        ultimo.Actualizar(resumen.Pendientes, resumen.Vencidos, resumen.Rechazados, ahora);
        return sinCambios;
    }

    public async Task ProcesarAsync(
        SugerenciaGestionCorreo sugerencia, Guid clienteId, bool esNotificacionAutomatica, CancellationToken cancellationToken = default)
    {
        // Nunca se demota un correo humano — solo mensajes ya identificados como
        // notificación automática de una plataforma conocida (Fase 1).
        if (!esNotificacionAutomatica || sugerencia.Detalles.Count == 0)
            return;

        var reclamacionIds = await reclamacionesContext.ReclamacionesDocumentales
            .Where(r => r.ClienteId == clienteId)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        if (reclamacionIds.Count == 0)
            return;

        var documentosReclamados = await reclamacionesContext.ReclamacionesDocumentalesDocumento
            .Where(rd => reclamacionIds.Contains(rd.ReclamacionDocumentalId))
            .Join(documentosContext.Documentos, rd => rd.DocumentoId, d => d.Id,
                (rd, d) => new DocumentoReclamadoDto(rd.Id, d.TrabajadorId, d.TipoDocumentoId))
            .ToListAsync(cancellationToken);

        var detalles = sugerencia.Detalles
            .Select(d => new DetalleParaClasificarDto(d.Id, d.TrabajadorId, d.TipoDocumentoId))
            .ToList();

        foreach (var (detalleId, reclamacionDocumentalDocumentoId) in ResolverRepeticiones(detalles, documentosReclamados))
            repositorio.Agregar(new ClasificacionRuidoDetalleGestion(detalleId, reclamacionDocumentalDocumentoId));
    }

    public record DetalleParaClasificarDto(Guid DetalleId, Guid? TrabajadorId, Guid? TipoDocumentoId);

    public record DocumentoReclamadoDto(Guid ReclamacionDocumentalDocumentoId, Guid? TrabajadorId, Guid? TipoDocumentoId);

    /// <summary>
    /// Extraído como método puro y estático (mismo patrón que
    /// <c>ObtenerBandejaGestorQueryHandler.Fusionar</c>) para poder probar el matching sin EF Core.
    /// Un ítem sin Trabajador o sin TipoDocumento resuelto nunca puede correlacionar — la IA no
    /// pudo identificarlo con certeza, así que tampoco hay con qué comparar.
    /// </summary>
    public static IReadOnlyDictionary<Guid, Guid> ResolverRepeticiones(
        IReadOnlyList<DetalleParaClasificarDto> detalles, IReadOnlyList<DocumentoReclamadoDto> documentosReclamados)
    {
        var resultado = new Dictionary<Guid, Guid>();

        foreach (var detalle in detalles)
        {
            if (detalle.TrabajadorId is null || detalle.TipoDocumentoId is null)
                continue;

            var coincidencia = documentosReclamados.FirstOrDefault(d =>
                d.TrabajadorId == detalle.TrabajadorId && d.TipoDocumentoId == detalle.TipoDocumentoId);

            if (coincidencia is not null)
                resultado[detalle.DetalleId] = coincidencia.ReclamacionDocumentalDocumentoId;
        }

        return resultado;
    }
}
