using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Verificacion;

/// <summary>
/// Lee por IA el archivo real de un Documento de Trabajador recién creado y
/// compara tipo/fecha de emisión/firma detectados contra lo introducido por
/// el usuario, generando una <see cref="RevisionIaDocumento"/> pendiente
/// cuando la confianza general es baja o hay una discrepancia — nunca
/// corrige nada automáticamente (ver Issue #19, "corrección automática" es
/// una pieza con componente legal, pendiente de confirmación explícita del
/// usuario). Solo corre si el TipoDocumento tiene la lectura IA activa a
/// Nivel 1 (Administrador) y la verificación activa (ver
/// TipoDocumento.VerificacionIaActiva) — a diferencia de
/// DeteccionTrabajadoresService (Fase 36), esta primera versión no
/// comprueba el Nivel 2 (ConfiguracionIaDocumentoCliente): un Documento de
/// Trabajador no tiene un ClienteId directo, y derivarlo exige recorrer sus
/// Asignaciones a Centro — deliberadamente fuera de alcance de esta fase
/// (ver ROADMAP.md).
///
/// <b>Fuente de referencia pendiente de usar</b> (PLAN-EJECUCION-UX.md § 0.7,
/// Lote 0-E): <see cref="TipoDocumento.CriteriosValidacion"/> es el texto de
/// criterios de aceptación copiado de la plataforma documental del cliente —
/// el campo que dice qué hace válido un documento de este tipo, en las
/// palabras del portal que lo va a aceptar o rechazar. Hoy esta comparación
/// solo mira tipo/fecha/firma; cuando se automatice la lectura contra los
/// criterios reales, ese campo es el insumo, no uno nuevo que haya que
/// modelar. Anotado aquí para que la sesión que lo implemente no tenga que
/// redescubrirlo.
///
/// <b>No confundir "no aplica" con "falló"</b> (D3): los primeros chequeos
/// de <see cref="ProcesarDocumentoAsync"/> (Documento sin archivo/Trabajador,
/// TipoDocumento sin verificación activa, revisión ya pendiente) terminan
/// en un <c>return</c> silencioso a propósito — son casos legítimos donde no
/// hay nada que verificar. Pero no abrir el archivo o que el proveedor de IA
/// devuelva un <c>Result</c> fallido SÍ es un fallo real de la verificación,
/// y por eso ambos casos lanzan en vez de retornar: dejan que
/// <c>ProcesadorAnalisisDocumentoHostedService</c> (Infrastructure, que ya
/// sabe reintentar, capturar en Sentry y avisar sin mentir) lo trate como lo
/// que es, en vez de que <c>MarcarCompletado()</c> + la campana "ya está
/// revisado" mientan sobre un documento que nunca llegó a leerse.
/// </summary>
public class VerificacionIaDocumentoService(
    IDocumentosQueryContext documentosContext, ITiposDocumentoQueryContext tiposDocumentoContext,
    IFileStorageService almacenamiento,
    IExtraccionMetadatosDocumentoIaService extraccion,
    IRevisionIaDocumentoRepository revisionRepositorio,
    IAprobacionDocumentoRepository aprobacionRepositorio,
    IAuditoriaExtraccionIaRepository auditoriaRepositorio,
    IUnitOfWork unitOfWork) : IVerificacionIaDocumentoService
{
    /// <summary>Por debajo de este umbral, la confianza general por sí sola ya justifica revisión humana (ver Issue #19, "70-95% revisar").</summary>
    private const int UmbralConfianzaBaja = 70;

    public async Task ProcesarDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default)
    {
        var documento = await documentosContext.Documentos.FirstOrDefaultAsync(d => d.Id == documentoId, cancellationToken);

        if (documento is null || documento.TrabajadorId is null || string.IsNullOrWhiteSpace(documento.ArchivoUrl))
            return;

        var tipoDocumento = await tiposDocumentoContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Id == documento.TipoDocumentoId, cancellationToken);

        if (tipoDocumento is null || !tipoDocumento.LecturaIaActiva || !tipoDocumento.VerificacionIaActiva)
            return;

        var yaHayRevisionPendiente = await documentosContext.RevisionesIaDocumento
            .AnyAsync(r => r.DocumentoId == documentoId && !r.Resuelta, cancellationToken);

        if (yaHayRevisionPendiente)
            return;

        byte[] contenido;
        try
        {
            await using var archivo = await almacenamiento.AbrirAsync(documento.ArchivoUrl, cancellationToken);
            using var buffer = new MemoryStream();
            await archivo.CopyToAsync(buffer, cancellationToken);
            contenido = buffer.ToArray();
        }
        catch (FileNotFoundException)
        {
            // Se relanza SIN envolver, a propósito: DiskFileStorageService y
            // S3FileStorageService ya normalizan a FileNotFoundException el
            // único caso realmente determinista de este catch (el archivo no
            // existe o no resuelve a este tenant) — no va a aparecer en un
            // segundo intento. Dejar pasar el tipo intacto es lo que permite
            // a ProcesadorAnalisisDocumentoHostedService reconocerlo y no
            // gastar los reintentos de TrabajoAnalisisDocumento.MaximoIntentos
            // (cada uno sería otro evento de Sentry sin ninguna posibilidad
            // de éxito) en vez de tragárselo como antes.
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cualquier otro fallo al abrir (red, el backend de almacenamiento
            // caído, un timeout) sí puede ser transitorio — se envuelve para
            // no perder el contexto, pero se deja reintentar como el resto.
            // No se traga el fallo: antes moría aquí con solo un log y el
            // trabajo seguía su curso hasta MarcarCompletado() como si el
            // documento sí se hubiera revisado (ver
            // ProcesadorAnalisisDocumentoHostedService, que interpreta "no
            // lanzó" como éxito y avisa al usuario "ya está revisado" sin que
            // se haya revisado nada — la trampa que Issue de D3 identificó).
            throw new InvalidOperationException(
                $"No se pudo abrir el archivo del Documento {documentoId} para verificación IA.", ex);
        }

        var resultadoExtraccion = await extraccion.ExtraerAsync(contenido, tipoDocumento.Nombre, documentoId, cancellationToken);

        if (resultadoExtraccion.EsFallido)
        {
            // Mismo criterio que el catch de arriba: un Result fallido del
            // proveedor de IA (proveedor caído, sin API key, respuesta
            // inválida...) no es "nada que revisar" — es un fallo real de la
            // verificación, y debe contar como tal en vez de como éxito.
            throw new InvalidOperationException(
                $"Verificación IA del Documento {documentoId} no disponible: {resultadoExtraccion.Error.Codigo} — {resultadoExtraccion.Error.Mensaje}");
        }

        var extraido = resultadoExtraccion.Valor;
        var motivos = ComputarMotivos(documento, extraido);

        if (motivos.Count == 0)
        {
            aprobacionRepositorio.Agregar(AprobacionDocumento.CrearAutomatica(documentoId, extraido.ConfianzaGeneral));
            await RegistrarDecisionEnAuditoriaAsync(documentoId, DecisionHumanaIa.AutomaticaSinRevision, usuarioId: null, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }

        var revision = RevisionIaDocumento.Crear(
            documentoId, extraido.ConfianzaGeneral, extraido.TipoDetectado,
            extraido.FechaEmisionDetectada, extraido.FechaVencimientoDetectada, extraido.TieneFirma,
            string.Join("; ", motivos));

        revisionRepositorio.Agregar(revision);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Cierra el ciclo "la IA propone, el humano dispone" sobre la
    /// <see cref="AuditoriaExtraccionIa"/> que el router de IA documental ya
    /// escribió para este Documento (ver RouterExtraccionMetadatosDocumentoIaService).
    /// Mejor esfuerzo: si no hay auditoría que enlazar (proveedor no
    /// configurado, o un Documento de antes de que existiera este enlace),
    /// no bloquea el flujo — la aprobación/revisión ya quedó registrada de
    /// todos modos por su propio camino.
    /// </summary>
    private async Task RegistrarDecisionEnAuditoriaAsync(Guid documentoId, DecisionHumanaIa decision, Guid? usuarioId, CancellationToken cancellationToken)
    {
        var auditoria = await auditoriaRepositorio.ObtenerUltimaSinDecisionPorDocumentoAsync(documentoId, cancellationToken);
        auditoria?.RegistrarDecisionHumana(decision, usuarioId);
    }

    private static List<string> ComputarMotivos(Documento documento, MetadatosDocumentoExtraidosDto extraido)
    {
        var motivos = new List<string>();

        if (extraido.ConfianzaGeneral < UmbralConfianzaBaja)
            motivos.Add($"Confianza baja ({extraido.ConfianzaGeneral}%)");

        if (extraido.FechaEmisionDetectada is { } fechaDetectada && fechaDetectada != documento.FechaEmision)
            motivos.Add("La fecha de emisión introducida no coincide con la detectada en el documento");

        if (extraido.TieneFirma == false)
            motivos.Add("No se detectó firma en el documento");

        return motivos;
    }
}
