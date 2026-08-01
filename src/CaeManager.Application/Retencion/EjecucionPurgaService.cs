using CaeManager.Application.Common;
using CaeManager.Domain.Retencion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Retencion;

/// <summary>
/// Ejecuta una purga <b>ya autorizada</b>: anonimiza lo que entraba en la
/// propuesta y borra los archivos correspondientes.
///
/// Es el único punto del sistema que destruye datos personales, y por eso no
/// decide nada por su cuenta. Comprueba dos cosas antes de tocar nada —que la
/// solicitud esté programada y que su fecha haya llegado— y ambas las impone
/// el propio agregado (<see cref="SolicitudPurga.Ejecutar"/>), no este
/// servicio: aunque alguien lo invocara por error, una propuesta sin autorizar
/// no se ejecuta.
///
/// Se usa la <see cref="SolicitudPurga.FechaCorte"/> guardada y no "hoy": la
/// propuesta se revisó y autorizó sobre un conjunto concreto de registros, y
/// ejecutarla no puede llevarse por delante nada que no estuviera en él por el
/// tiempo transcurrido entre la autorización y la ejecución.
/// </summary>
public class EjecucionPurgaService(
    IApplicationDbContext dbContext,
    ISolicitudPurgaRepository solicitudRepositorio,
    IFileStorageService almacenamiento,
    ITenantActual tenantActual,
    IUnitOfWork unitOfWork,
    ILogger<EjecucionPurgaService> logger)
{
    /// <summary>Devuelve cuántos registros se anonimizaron.</summary>
    public async Task<int> EjecutarAsync(Guid solicitudId, DateOnly hoy, CancellationToken cancellationToken = default)
    {
        // Fallo cerrado — mismo criterio que DeteccionPurgaService: sin
        // tenant resuelto no hay ámbito seguro en el que ignorar el filtro
        // de soft-delete más abajo.
        if (tenantActual.TenantId is not { } tenantId) return 0;

        var solicitud = await solicitudRepositorio.ObtenerPorIdAsync(solicitudId, cancellationToken);
        if (solicitud is null) return 0;

        // Lanza si no está autorizada o si no ha llegado la fecha. Se deja
        // propagar a propósito: llegar aquí sin autorización es un error de
        // programación, no un caso de negocio que haya que tragar.
        solicitud.Ejecutar(hoy);

        var ahora = DateTime.UtcNow;

        var afectados = solicitud.TipoDato switch
        {
            TipoDatoPurgable.Documentos => await AnonimizarDocumentosAsync(tenantId, solicitud.FechaCorte, ahora, cancellationToken),
            TipoDatoPurgable.TrabajadoresDadosDeBaja => await AnonimizarTrabajadoresAsync(tenantId, solicitud.FechaCorte, ahora, cancellationToken),
            _ => 0
        };

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Purga ejecutada: {Afectados} registros de tipo {Tipo} anonimizados (solicitud {SolicitudId}).",
            afectados, solicitud.TipoDato, solicitud.Id);

        return afectados;
    }

    private async Task<int> AnonimizarDocumentosAsync(
        Guid tenantId, DateOnly fechaCorte, DateTime ahora, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters() + Where(TenantId) explícito — ver el comentario
        // equivalente en DeteccionPurgaService (P0-1/P0-3 de
        // docs/business/MATURITY_REVIEW.md): sin esto, un Documento
        // soft-deleted que SÍ hubiera entrado en la SolicitudPurga (porque la
        // detección ya lo ve, tras el fix de arriba) seguiría sin
        // anonimizarse aquí, dejando la purga incompleta a medio camino.
        var documentos = await dbContext.Documentos
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId)
            .Where(d => d.AnonimizadoEnUtc == null)
            .Where(d => d.FechaVencimiento != null
                ? d.FechaVencimiento <= fechaCorte
                : d.FechaEmision <= fechaCorte)
            .ToListAsync(cancellationToken);

        foreach (var documento in documentos)
        {
            var archivo = documento.Anonimizar(ahora);
            if (archivo is null) continue;

            // El fichero se borra aparte porque el dato personal está dentro
            // del PDF: sin esto la fila quedaría limpia y el documento
            // seguiría en disco.
            try
            {
                await almacenamiento.EliminarAsync(archivo, cancellationToken);
            }
            catch (Exception ex)
            {
                // No se aborta la purga entera por un archivo: el resto de la
                // supresión sigue siendo válida y necesaria. Pero queda
                // constancia, porque un archivo superviviente es exactamente
                // lo que esta operación existe para evitar.
                logger.LogError(ex,
                    "No se pudo borrar el archivo {Archivo} del documento {DocumentoId} durante la purga.",
                    archivo, documento.Id);
            }
        }

        return documentos.Count;
    }

    private async Task<int> AnonimizarTrabajadoresAsync(
        Guid tenantId, DateOnly fechaCorte, DateTime ahora, CancellationToken cancellationToken)
    {
        // Mismo criterio que AnonimizarDocumentosAsync — ver comentario ahí.
        var trabajadores = await dbContext.Trabajadores
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId)
            .Where(t => t.AnonimizadoEnUtc == null)
            .Where(t => dbContext.Asignaciones.Any(a => a.TrabajadorId == t.Id))
            .Where(t => !dbContext.Asignaciones.Any(a => a.TrabajadorId == t.Id && a.FechaBaja == null))
            .Where(t => dbContext.Asignaciones
                .Where(a => a.TrabajadorId == t.Id)
                .Max(a => a.FechaBaja) <= fechaCorte)
            .ToListAsync(cancellationToken);

        foreach (var trabajador in trabajadores)
            trabajador.Anonimizar(ahora);

        return trabajadores.Count;
    }
}
