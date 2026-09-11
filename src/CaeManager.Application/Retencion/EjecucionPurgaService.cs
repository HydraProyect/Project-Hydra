using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.DocumentosIa;
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
    IAsignacionesQueryContext asignacionesContext, IDocumentosQueryContext documentosContext, ITrabajadoresQueryContext trabajadoresContext,
    ISolicitudPurgaRepository solicitudRepositorio,
    IExtraccionIaCacheRepository extraccionIaCacheRepositorio,
    IFileStorageService almacenamiento,
    ITenantActual tenantActual,
    IUnitOfWork unitOfWork,
    IAlertaOperativa alertaOperativa,
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

        var resultado = solicitud.TipoDato switch
        {
            TipoDatoPurgable.Documentos => await AnonimizarDocumentosAsync(tenantId, solicitud.Id, solicitud.FechaCorte, ahora, cancellationToken),
            TipoDatoPurgable.TrabajadoresDadosDeBaja => await AnonimizarTrabajadoresAsync(tenantId, solicitud.FechaCorte, ahora, cancellationToken),
            _ => new ResultadoAnonimizacion(0, 0, [])
        };

        foreach (var incidencia in resultado.Incidencias)
            solicitudRepositorio.AgregarIncidencia(incidencia);

        // Resultado durable de ESTA ejecución, aparte de Estado — ver
        // SolicitudPurga.RegistrarResultadoEjecucion. Persistido en el mismo
        // SaveChangesAsync que el cambio de estado y las anonimizaciones: no
        // hay ventana en la que uno se guarde sin el otro.
        solicitud.RegistrarResultadoEjecucion(resultado.Candidatos, resultado.Suprimidos, resultado.Fallidos);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Purga ejecutada: {Suprimidos}/{Candidatos} registros de tipo {Tipo} anonimizados, {Fallidos} con incidencias (solicitud {SolicitudId}).",
            resultado.Suprimidos, resultado.Candidatos, solicitud.TipoDato, resultado.Fallidos, solicitud.Id);

        return resultado.Suprimidos;
    }

    /// <summary>Cuántos candidatos había, cuántos se suprimieron y las incidencias de los que no.</summary>
    private readonly record struct ResultadoAnonimizacion(int Candidatos, int Suprimidos, IReadOnlyList<IncidenciaPurga> Incidencias)
    {
        public int Fallidos => Candidatos - Suprimidos;
    }

    private async Task<ResultadoAnonimizacion> AnonimizarDocumentosAsync(
        Guid tenantId, Guid solicitudId, DateOnly fechaCorte, DateTime ahora, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters() + Where(TenantId) explícito — ver el comentario
        // equivalente en DeteccionPurgaService (P0-1/P0-3 de
        // docs/business/MATURITY_REVIEW.md): sin esto, un Documento
        // soft-deleted que SÍ hubiera entrado en la SolicitudPurga (porque la
        // detección ya lo ve, tras el fix de arriba) seguiría sin
        // anonimizarse aquí, dejando la purga incompleta a medio camino.
        var documentos = await documentosContext.Documentos
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId)
            .Where(d => d.AnonimizadoEnUtc == null)
            .Where(d => d.FechaVencimiento != null
                ? d.FechaVencimiento <= fechaCorte
                : d.FechaEmision <= fechaCorte)
            .ToListAsync(cancellationToken);

        // El orden importa y antes estaba al revés. Anonimizar primero borra
        // ArchivoUrl de la fila; si el borrado del fichero fallaba después, la
        // purga seguía adelante y confirmaba: el documento quedaba marcado como
        // anonimizado, el PDF seguía en almacenamiento y la única referencia
        // que permitía encontrarlo ya no existía. Es decir, un reconocimiento
        // médico conservado para siempre y declarado suprimido — conformidad
        // falsa, y además irreparable, porque ningún reintento sabría ya qué
        // borrar.
        //
        // Ahora se borra el fichero ANTES de soltar su referencia y solo se
        // anonimiza si el borrado salió bien. Un fallo deja el documento
        // intacto, con su ArchivoUrl, de modo que sigue siendo localizable y
        // una purga posterior puede volver a intentarlo.
        var anonimizados = 0;
        var idsAnonimizados = new List<Guid>();
        var noSuprimidos = new List<Guid>();
        var incidencias = new List<IncidenciaPurga>();

        foreach (var documento in documentos)
        {
            if (documento.ArchivoUrl is { } archivo)
            {
                try
                {
                    await almacenamiento.EliminarAsync(archivo, cancellationToken);
                }
                catch (Exception ex)
                {
                    // No se aborta la purga entera por un archivo: el resto de
                    // la supresión sigue siendo válida y necesaria. Este
                    // documento se queda como estaba. El detalle de la
                    // excepción va al log, no a IncidenciaPurga — esa es
                    // durable y no debe llevar rutas de almacenamiento ni
                    // otros detalles internos crudos.
                    logger.LogError(ex,
                        "No se pudo borrar el archivo del documento {DocumentoId} durante la purga: se deja sin anonimizar para que un reintento posterior pueda encontrarlo.",
                        documento.Id);
                    noSuprimidos.Add(documento.Id);
                    incidencias.Add(IncidenciaPurga.Crear(
                        solicitudId, documento.Id, TipoIncidenciaPurga.FalloEliminacionArchivo,
                        "No se pudo eliminar el archivo del almacenamiento durante la purga."));
                    continue;
                }
            }

            documento.Anonimizar(ahora);
            anonimizados++;
            idsAnonimizados.Add(documento.Id);
        }

        // REC-036/DEC-34: la caché de extracción IA entra en el ciclo de vida
        // del Documento aquí, no en el borrado lógico reversible — ver el
        // comentario de ExtraccionIaCacheDocumento. Solo los Documentos que
        // de verdad se anonimizaron (idsAnonimizados, no "documentos": un
        // fallo de borrado de archivo deja el Documento intacto y su caché
        // no debe tocarse).
        if (idsAnonimizados.Count > 0)
            await extraccionIaCacheRepositorio.PurgarVinculadosADocumentosAsync(idsAnonimizados, cancellationToken);

        if (noSuprimidos.Count > 0)
        {
            // La solicitud ya se marcó ejecutada arriba (SolicitudPurga.Ejecutar):
            // nada va a reintentar esto automáticamente todavía. La alerta
            // sigue siendo el aviso inmediato, pero ya no es el único rastro
            // — cada documento sin suprimir queda también en IncidenciaPurga,
            // correlacionado con esta solicitud (SolicitudPurga.ResultadoEjecucion
            // = ConIncidencias), para que un futuro outbox de reintentos no
            // tenga que reinventar qué falló. El reintento automático en sí
            // sigue siendo la decisión de arquitectura pendiente, compartida
            // con el Módulo 7 (ver el informe del Módulo 2).
            alertaOperativa.Emitir(
                $"Purga {solicitudId}: {noSuprimidos.Count} documento(s) no se pudieron suprimir y quedan sin anonimizar. " +
                $"Ids: {string.Join(", ", noSuprimidos)}.",
                NivelAlertaOperativa.Critica);
        }

        return new ResultadoAnonimizacion(documentos.Count, anonimizados, incidencias);
    }

    private async Task<ResultadoAnonimizacion> AnonimizarTrabajadoresAsync(
        Guid tenantId, DateOnly fechaCorte, DateTime ahora, CancellationToken cancellationToken)
    {
        // Mismo criterio que AnonimizarDocumentosAsync — ver comentario ahí.
        var trabajadores = await trabajadoresContext.Trabajadores
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId)
            .Where(t => t.AnonimizadoEnUtc == null)
            .Where(t => asignacionesContext.Asignaciones.Any(a => a.TrabajadorId == t.Id))
            .Where(t => !asignacionesContext.Asignaciones.Any(a => a.TrabajadorId == t.Id && a.FechaBaja == null))
            .Where(t => asignacionesContext.Asignaciones
                .Where(a => a.TrabajadorId == t.Id)
                .Max(a => a.FechaBaja) <= fechaCorte)
            .ToListAsync(cancellationToken);

        foreach (var trabajador in trabajadores)
            trabajador.Anonimizar(ahora);

        // Sin paso de almacenamiento externo — anonimizar un Trabajador no
        // puede fallar a medias hoy, así que no hay incidencias que registrar.
        return new ResultadoAnonimizacion(trabajadores.Count, trabajadores.Count, []);
    }
}
