using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Verificacion;

/// <summary>
/// Lee por IA el archivo real de un Documento de Trabajador recién creado y
/// contrasta fecha de emisión y firma detectadas contra lo introducido por
/// el usuario, generando una <see cref="RevisionIaDocumento"/> pendiente
/// cuando la confianza general es baja, cuando hay una discrepancia o
/// cuando falta la evidencia que haría falta para aprobar (ver
/// <see cref="ComputarMotivos"/>) — nunca
/// corrige nada automáticamente (ver Issue #19, "corrección automática" es
/// una pieza con componente legal, pendiente de confirmación explícita del
/// usuario). Solo corre si el Tenant propietario tiene una instrucción
/// documentada vigente de tratamiento con IA (Nivel 0, DEC-33/REC-035, ver
/// <see cref="IInstruccionTratamientoIaService"/>), si el TipoDocumento
/// tiene la lectura IA activa a Nivel 1 (Administrador) y la verificación
/// activa (ver TipoDocumento.VerificacionIaActiva) — a diferencia de
/// DeteccionTrabajadoresService (Fase 36), esta primera versión no
/// comprueba el Nivel 2 (ConfiguracionIaDocumentoCliente): un Documento de
/// Trabajador no tiene un ClienteId directo, y derivarlo exige recorrer sus
/// Asignaciones a Centro — deliberadamente fuera de alcance de esta fase
/// (ver Project-Hydra-Negocio/tecnico/ROADMAP.md).
///
/// <b>Fuente de referencia pendiente de usar</b> (Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.7,
/// Lote 0-E): <see cref="TipoDocumento.CriteriosValidacion"/> es el texto de
/// criterios de aceptación copiado de la plataforma documental del cliente —
/// el campo que dice qué hace válido un documento de este tipo, en las
/// palabras del portal que lo va a aceptar o rechazar. Hoy esta comparación
/// solo mira fecha y firma; cuando se automatice la lectura contra los
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
///
/// <b>Una decisión manual siempre prevalece</b>: si entre el encolado y la
/// escritura alguien decidió el Documento a mano (una AprobacionDocumento
/// manual) o el Documento dejó de ser el que se encoló (otra
/// <c>Version</c>: renovación, corrección, archivo nuevo, eliminación,
/// anonimización), el resultado se descarta sin escribir nada y se devuelve
/// el motivo, con el que <c>ProcesadorAnalisisDocumentoHostedService</c>
/// marca el trabajo <c>Descartado</c>. Se comprueba dos veces: antes de leer
/// el archivo (para no pagar la IA) y dentro de la transacción que escribe,
/// con la fila del Documento bloqueada (ver
/// <see cref="ITransaccionDocumentoBloqueado"/>) — la que cuenta.
/// </summary>
public class VerificacionIaDocumentoService(
    IDocumentosQueryContext documentosContext, ITiposDocumentoQueryContext tiposDocumentoContext,
    IFileStorageService almacenamiento,
    IExtraccionMetadatosDocumentoIaService extraccion,
    IRevisionIaDocumentoRepository revisionRepositorio,
    IAprobacionDocumentoRepository aprobacionRepositorio,
    IAuditoriaExtraccionIaRepository auditoriaRepositorio,
    IInstruccionTratamientoIaService instruccionTratamientoIa,
    ITenantActual tenantActual,
    ITransaccionDocumentoBloqueado transaccion,
    IUnitOfWork unitOfWork) : IVerificacionIaDocumentoService
{
    /// <summary>Por debajo de este umbral, la confianza general por sí sola ya justifica revisión humana (ver Issue #19, "70-95% revisar").</summary>
    private const int UmbralConfianzaBaja = 70;

    public async Task<string?> ProcesarDocumentoAsync(EncargoVerificacionIa encargo, CancellationToken cancellationToken = default)
    {
        var documentoId = encargo.DocumentoId;

        // Nivel 0 (DEC-33, REC-035), por delante de los niveles 1 y 2 que ya
        // existían: sin instrucción documentada vigente para este tenant, el
        // tratamiento con IA no se activa — sea cual sea la configuración de
        // TipoDocumento/ConfiguracionIaDocumentoCliente. tenantActual.TenantId
        // nulo (sin tenant resuelto) también falla cerrado, mismo criterio
        // que el resto del sistema.
        if (tenantActual.TenantId is not { } tenantId || !await instruccionTratamientoIa.EstaHabilitadaAsync(tenantId, cancellationToken))
            return null;

        var documento = await documentosContext.Documentos.FirstOrDefaultAsync(d => d.Id == documentoId, cancellationToken);

        if (documento is null || documento.TrabajadorId is null || string.IsNullOrWhiteSpace(documento.ArchivoUrl))
            return null;

        var tipoDocumento = await tiposDocumentoContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Id == documento.TipoDocumentoId, cancellationToken);

        if (tipoDocumento is null || !tipoDocumento.LecturaIaActiva || !tipoDocumento.VerificacionIaActiva)
            return null;

        var yaHayRevisionPendiente = await documentosContext.RevisionesIaDocumento
            .AnyAsync(r => r.DocumentoId == documentoId && !r.Resuelta, cancellationToken);

        if (yaHayRevisionPendiente)
            return null;

        // Primera comprobación, sin bloqueo: si el resultado ya se sabe
        // descartable, no se paga la llamada al proveedor de IA. No sustituye
        // a la de dentro de la transacción, que es la que cuenta.
        var motivoDescartePrevio = await MotivoDescarteAsync(encargo, documento.Version, cancellationToken);
        if (motivoDescartePrevio is not null)
            return motivoDescartePrevio;

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
            // Se relanza SIN envolver, a propósito: DiskFileStorageService
            // ya normaliza a FileNotFoundException el
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

        // Las entidades se construyen fuera de la transacción: si la
        // estrategia de reintentos repite la operación, volver a Agregar la
        // misma instancia no la duplica.
        var aprobacion = motivos.Count == 0
            ? AprobacionDocumento.CrearAutomatica(documentoId, extraido.ConfianzaGeneral)
            : null;
        var revision = motivos.Count == 0
            ? null
            : RevisionIaDocumento.Crear(
                documentoId, extraido.ConfianzaGeneral, extraido.TipoDetectado,
                extraido.FechaEmisionDetectada, extraido.FechaVencimientoDetectada, extraido.TieneFirma,
                string.Join("; ", motivos));

        // La comprobación que decide y la escritura, en la misma transacción
        // y con la fila del Documento bloqueada: una decisión manual
        // concurrente o se confirma antes (y aquí se ve) o espera a que esto
        // termine. Una decisión manual siempre prevalece sobre la automática.
        return await transaccion.EjecutarAsync(documentoId, async (versionActual, ct) =>
        {
            var motivoDescarte = await MotivoDescarteAsync(encargo, versionActual, ct);
            if (motivoDescarte is not null)
                return motivoDescarte;

            // La auditoría se escribió al terminar esta extracción.
            var auditoria = await auditoriaRepositorio.ObtenerUltimaSinDecisionPorDocumentoAsync(documentoId, ct);

            if (aprobacion is not null)
            {
                aprobacionRepositorio.Agregar(aprobacion);
                RegistrarDecisionAutomaticaEnAuditoria(auditoria);
            }
            else
            {
                // Se guarda el Id de la auditoría en la revisión para que
                // lecturas y decisiones posteriores nunca sustituyan una
                // extracción por otra del mismo documento.
                if (auditoria is not null && revision!.AuditoriaExtraccionIaId is null)
                    revision.VincularAuditoriaExtraccionIa(auditoria.Id);

                revisionRepositorio.Agregar(revision!);
            }

            await unitOfWork.SaveChangesAsync(ct);
            return (string?)null;
        }, cancellationToken);
    }

    /// <summary>
    /// Por qué el resultado de esta verificación ya no puede aplicarse, o
    /// <c>null</c> si sí. Tres motivos, todos de "llegó tarde": el Documento
    /// desapareció, cambió de versión desde que se encoló (renovación,
    /// corrección, archivo nuevo, anonimización), o alguien lo decidió a mano
    /// desde entonces (una AprobacionDocumento manual: resolver, corregir o
    /// aplicar una revisión). La decisión manual anterior al encolado no
    /// cuenta: es la que esta verificación viene a revisar.
    /// </summary>
    private async Task<string?> MotivoDescarteAsync(EncargoVerificacionIa encargo, Guid? versionActual, CancellationToken cancellationToken)
    {
        if (versionActual is not { } version)
            return "El Documento ya no existe o fue eliminado antes de aplicar la verificación.";

        if (encargo.VersionDocumentoEncolada is { } versionEncolada && versionEncolada != version)
            return "El Documento cambió después de encolar la verificación (renovación, corrección, archivo nuevo o anonimización): el resultado corresponde a una versión anterior.";

        var hayDecisionManualPosterior = await documentosContext.AprobacionesDocumento.AnyAsync(
            a => a.DocumentoId == encargo.DocumentoId
                && a.Tipo == TipoAprobacionDocumento.Manual
                && a.CreadaEnUtc >= encargo.EncoladoEnUtc,
            cancellationToken);

        return hayDecisionManualPosterior
            ? "Un usuario decidió el Documento a mano después de encolar la verificación."
            : null;
    }

    /// <summary>
    /// Cierra el ciclo "la IA propone, el humano dispone" sobre la
    /// <see cref="AuditoriaExtraccionIa"/> que el router de IA documental ya
    /// escribió para este Documento (ver RouterExtraccionMetadatosDocumentoIaService).
    /// Mejor esfuerzo: si no hay auditoría que enlazar (proveedor no
    /// configurado, o un Documento de antes de que existiera este enlace),
    /// no bloquea el flujo — la aprobación ya quedó registrada de todos modos
    /// por su propio camino. Si la operación se repite por un reintento, la
    /// auditoría ya lleva la decisión en memoria y no se vuelve a registrar.
    /// </summary>
    private static void RegistrarDecisionAutomaticaEnAuditoria(AuditoriaExtraccionIa? auditoria)
    {
        if (auditoria is { DecisionHumana: null })
            auditoria.RegistrarDecisionHumana(DecisionHumanaIa.AutomaticaSinRevision, usuarioId: null);
    }

    /// <summary>
    /// La aprobación automática exige <b>evidencia positiva</b>, no la mera
    /// ausencia de discrepancias. La salida de un LLM es una propuesta, no una
    /// prueba: el texto del documento entra en el prompt sin separación de
    /// canal, así que un PDF preparado puede pedir "devuelve confianza 100" y
    /// obtenerlo. Por eso ningún campo autorreportado puede, por sí solo,
    /// cerrar un documento sin que lo mire nadie.
    ///
    /// Antes esta comparación solo tenía tres reglas y todas exigían que el
    /// dato <em>existiera</em> para poder discrepar: una extracción con
    /// confianza ≥70, <c>FechaEmisionDetectada = null</c> y
    /// <c>TieneFirma = null</c> devolvía cero motivos y el documento quedaba
    /// aprobado automáticamente. Es decir: cuanto menos entendía la IA el
    /// archivo, más fácil lo aprobaba — un archivo ilegible, un documento que
    /// no es el que dice ser, o una respuesta truncada del proveedor pasaban
    /// como éxito. Ahora cada dato ausente es un motivo de revisión, no un
    /// silencio a favor.
    ///
    /// <b>Lo que sigue sin comprobarse, y por qué</b>: <c>TipoDetectado</c> se
    /// exige presente, pero NO se coteja contra
    /// <see cref="TipoDocumento.Nombre"/>. No es un olvido. El modelo devuelve
    /// el título tal como aparece impreso en el documento ("Apto médico",
    /// "Reconocimiento médico favorable") mientras el catálogo usa nombres
    /// administrativos ("Certificado de aptitud médica"): compararlos por
    /// texto marcaría como discrepancia la inmensa mayoría de los documentos
    /// correctos y convertiría la revisión humana en ruido, que es otra forma
    /// de no revisar nada. Hacerlo bien exige lo que hoy no existe en el
    /// modelo — un código estable o una lista de denominaciones equivalentes
    /// por <see cref="TipoDocumento"/> — y calibrarlo contra documentos
    /// reales. Es un incremento propio, con decisión de producto detrás; hasta
    /// entonces queda escrito aquí para que nadie lo dé por cubierto.
    /// </summary>
    private static List<string> ComputarMotivos(Documento documento, MetadatosDocumentoExtraidosDto extraido)
    {
        var motivos = new List<string>();

        if (extraido.ConfianzaGeneral < UmbralConfianzaBaja)
            motivos.Add($"Confianza baja ({extraido.ConfianzaGeneral}%)");

        if (string.IsNullOrWhiteSpace(extraido.TipoDetectado))
            motivos.Add("No se pudo determinar el tipo real del documento");

        if (extraido.FechaEmisionDetectada is not { } fechaDetectada)
            motivos.Add("No se pudo verificar la fecha de emisión en el documento");
        else if (fechaDetectada != documento.FechaEmision)
            motivos.Add("La fecha de emisión introducida no coincide con la detectada en el documento");

        // null y false se tratan igual a propósito: "no hay firma" y "no se
        // sabe si hay firma" son ambos ausencia de prueba de firma, y ninguno
        // de los dos puede sostener una aprobación sin revisión.
        if (extraido.TieneFirma is not true)
            motivos.Add("No hay evidencia de firma en el documento");

        return motivos;
    }
}
