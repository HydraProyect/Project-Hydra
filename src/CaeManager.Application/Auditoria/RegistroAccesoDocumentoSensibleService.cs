using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Auditoria;

/// <inheritdoc cref="IRegistroAccesoDocumentoSensibleService"/>
public class RegistroAccesoDocumentoSensibleService(
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IActorAuditoria actorAuditoria,
    IRegistroAccesoDocumentoSensibleRepository repositorio,
    CaeManager.Application.Seguridad.Incidentes.ISenalesIncidenteSeguridad? senalesIncidente = null,
    ITenantActual? tenantActual = null,
    // Solo para la comprobación de «ya registrado» de RegistrarRevisionIaSiSensibleAsync. Sin él, esa vía registra en
    // cada llamada: de más, nunca de menos.
    IAuditoriaQueryContext? auditoriaContext = null,
    ILogger<RegistroAccesoDocumentoSensibleService>? logger = null)
    : IRegistroAccesoDocumentoSensibleService
{
    public async Task RegistrarSiSensibleAsync(
        Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default)
    {
        if (await RegistrarSinSenalAsync(documentoId, tipoAcceso, cancellationToken) is { } senal)
            await EmitirAsync(senal, cancellationToken);
    }

    public async Task RegistrarRevisionIaSiSensibleAsync(
        Guid documentoId, DateTime revisionCreadaEnUtc, CancellationToken cancellationToken = default)
    {
        if (await RegistrarSinSenalAsync(documentoId, TipoAccesoDocumentoSensible.RevisionIa, cancellationToken, revisionCreadaEnUtc) is { } senal)
            await EmitirAsync(senal, cancellationToken);
    }

    public async Task RegistrarSiSensibleAsync(
        Guid recursoId, Guid tipoDocumentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default)
    {
        if (await RegistrarSinSenalAsync(recursoId, tipoDocumentoId, tipoAcceso, cancellationToken) is { } senal)
            await EmitirAsync(senal, cancellationToken);
    }

    public ILoteAccesosSensibles IniciarLote() => new LoteDeAccesos(this);

    /// <summary>
    /// Lo que ya consta registrado y falta anunciar a la señal de extracción masiva: quién y en qué Tenant activo. Se calcula
    /// al registrar, no al emitir, para que la identidad sea la del momento del acceso.
    /// </summary>
    private readonly record struct SenalPendiente(Guid ActorRealUsuarioId, Guid? TenantId);

    private async Task EmitirAsync(SenalPendiente senal, CancellationToken cancellationToken)
    {
        if (senalesIncidente is not null)
            await senalesIncidente.DocumentoSensibleAbiertoAsync(senal.ActorRealUsuarioId, senal.TenantId, cancellationToken);
    }

    /// <summary>
    /// Aperturas de un mismo paquete dentro de una transacción de varios guardados: cada una se registra al momento (la
    /// transacción las agrupa), pero la señal de extracción masiva —un contador en memoria que ningún rollback deshace— se
    /// retiene hasta <see cref="EmitirSenalesAsync"/>, que quien abrió el lote llama solo tras confirmar. Un lote por intento.
    /// </summary>
    private sealed class LoteDeAccesos(RegistroAccesoDocumentoSensibleService servicio) : ILoteAccesosSensibles
    {
        private readonly List<SenalPendiente> _pendientes = [];

        public async Task RegistrarSiSensibleAsync(
            Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default)
        {
            if (await servicio.RegistrarSinSenalAsync(documentoId, tipoAcceso, cancellationToken) is { } senal)
                _pendientes.Add(senal);
        }

        public async Task EmitirSenalesAsync(CancellationToken cancellationToken = default)
        {
            var pendientes = _pendientes.ToArray();
            _pendientes.Clear();
            foreach (var senal in pendientes)
                await servicio.EmitirAsync(senal, cancellationToken);
        }
    }

    private async Task<SenalPendiente?> RegistrarSinSenalAsync(
        Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken,
        DateTime? deduplicarDesdeUtc = null)
    {
        // Un solo roundtrip adicional: el TipoDocumentoId del Documento y, con
        // él, la Sensibilidad del catálogo — el punto único de consulta de
        // REC-132. Sin navegación EF (Documento no expone TipoDocumento como
        // navegación cargable aquí); dos consultas encadenadas, no un join,
        // porque el segundo paso solo hace falta cuando el primero resuelve.
        var tipoDocumentoId = await documentosContext.Documentos
            .Where(d => d.Id == documentoId)
            .Select(d => (Guid?)d.TipoDocumentoId)
            .FirstOrDefaultAsync(cancellationToken);

        // El Documento ya no se pudo resolver (baja física, caso raro fuera
        // de la retención ordinaria): no hay categoría que consultar, así que
        // se asume la más protectora en vez de omitir el registro — ver
        // RegistrarSiSensibleAsync en la interfaz.
        if (tipoDocumentoId is not { } id)
        {
            return await RegistrarAsync(documentoId, SensibilidadDocumental.CategoriaEspecialSalud, tipoAcceso, cancellationToken, deduplicarDesdeUtc);
        }

        return await RegistrarSinSenalAsync(documentoId, id, tipoAcceso, cancellationToken, deduplicarDesdeUtc);
    }

    private async Task<SenalPendiente?> RegistrarSinSenalAsync(
        Guid recursoId, Guid tipoDocumentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken,
        DateTime? deduplicarDesdeUtc = null)
    {
        var sensibilidadResuelta = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.Id == tipoDocumentoId)
            .Select(t => (SensibilidadDocumental?)t.Sensibilidad)
            .FirstOrDefaultAsync(cancellationToken);

        // El TipoDocumento tampoco resuelve (catálogo editado entre medias,
        // caso raro): mismo criterio protector que el Documento no resuelto.
        var sensibilidad = sensibilidadResuelta ?? SensibilidadDocumental.CategoriaEspecialSalud;

        return await RegistrarAsync(recursoId, sensibilidad, tipoAcceso, cancellationToken, deduplicarDesdeUtc);
    }

    private async Task<SenalPendiente?> RegistrarAsync(
        Guid recursoId, SensibilidadDocumental sensibilidad, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken,
        DateTime? deduplicarDesdeUtc = null)
    {
        // "Nunca registrar de más" (DEC-36): un documento sin datos
        // personales no genera fila.
        if (sensibilidad == SensibilidadDocumental.SinDatosPersonales)
            return null;

        var actor = await actorAuditoria.ObtenerAsync();

        var registro = new RegistroAccesoDocumentoSensible(
            recursoId,
            sensibilidad,
            tipoAcceso,
            actor.UsuarioSimuladoId ?? actor.ActorRealUsuarioId,
            actor.ActorRealUsuarioId,
            (TipoViaAccesoAuditoria)actor.Via,
            actor.ViaAccesoId,
            (TipoActorAuditoria)actor.ResolverTipoActor());

        // Señal de extracción masiva (documentos sensibles abiertos o descargados por el actor real). Se devuelve en vez de
        // emitirse aquí para que quien agrupa varios registros en una transacción la emita solo tras confirmar.
        SenalPendiente? senal = actor.ActorRealUsuarioId is { } actorReal ? new SenalPendiente(actorReal, tenantActual?.TenantId) : null;

        // Revisión IA: la fila se deduplica, no una por GET (el visor pide por rangos y se recarga); la señal, no. Como mucho
        // una fila por persona y revisión pendiente: con una sola revisión pendiente del Documento se deduplica por persona y
        // Documento desde que se creó —la misma fila ya escrita: mismo Tenant, Documento, tipo, Usuario, Actor real y vía—;
        // con varias, cada acceso deja fila, porque la fila no identifica la revisión y la de una taparía la otra.
        // Peticiones simultáneas pueden dejar más, nunca menos: la comprobación y la escritura no van bajo un bloqueo, y la
        // auditoría solo puede pecar por exceso. Cada acceso cuenta para la señal de extracción masiva, se escriba la fila o
        // no: la misma paridad que Apertura, donde cada GET suma, y sin depender de lo que el contador en memoria de la señal
        // recuerde (se vacía al reiniciar el proceso; las filas, no).
        if (deduplicarDesdeUtc is { } desde && await YaRegistradoAsync(registro, desde, cancellationToken))
            return senal;

        // Falla en cerrado (DEC-36, REC-208): un acceso sensible que no queda
        // registrado no se sirve. El repositorio traduce el rechazo de la base
        // (Sesión Privilegiada que la función de registro no reconoce, conexión
        // sin privilegio) a false — Application no puede distinguirlo sin
        // conocer el proveedor de EF, ver IRegistroAccesoDocumentoSensibleRepository —,
        // y aquí se convierte en una excepción que corta la respuesta.
        if (!await repositorio.GuardarAsync(registro, cancellationToken))
            throw new AccesoDocumentoSensibleNoRegistradoException(recursoId);

        // La señal, solo tras quedar registrado el acceso (o constar ya registrado, arriba).
        return senal;
    }

    /// <summary>
    /// ¿Consta ya la fila que <paramref name="registro"/> escribiría, ocurrida desde <paramref name="desdeUtc"/>? Lee por
    /// <see cref="IAuditoriaQueryContext"/>, con el filtro de Tenant y la RLS de la tabla, y además compara el Tenant actual
    /// de forma explícita: una fila de otro Tenant propietario no tapa nunca este acceso, aunque una de esas dos barreras
    /// faltara. Con más de una revisión pendiente del Documento (<see cref="ColaRevisionIa.Pendientes"/>) contesta que no:
    /// la fila no dice de qué revisión es. Falla en cerrado hacia el registro: sin contexto, sin Tenant actual, sin Actor
    /// real (no hay de quién deduplicar) o si alguna de las dos consultas falla, contesta que no, y se escribe la fila. Un falso «ya registrado» dejaría el acceso
    /// sin rastro; una fila de más, no. Solo decide la fila: la señal de extracción masiva se avisa en los dos casos.
    /// </summary>
    private async Task<bool> YaRegistradoAsync(RegistroAccesoDocumentoSensible registro, DateTime desdeUtc, CancellationToken cancellationToken)
    {
        if (auditoriaContext is null || registro.ActorRealUsuarioId is null || tenantActual?.TenantId is not { } tenantId)
            return false;

        try
        {
            // La fila no identifica la revisión: con varias revisiones pendientes del Documento, la fila de una taparía el
            // acceso por otra. Entonces no se deduplica y cada acceso deja fila. «Pendiente» es la de la cola
            // (ColaRevisionIa.Pendientes), sin acotar por cartera: contar de más solo lleva a escribir la fila.
            var revisionesPendientesDelDocumento = await ColaRevisionIa.Pendientes(documentosContext, trabajadorIdsVisibles: null)
                .Where(p => p.Revision.DocumentoId == registro.DocumentoId)
                .CountAsync(cancellationToken);
            if (revisionesPendientesDelDocumento > 1)
                return false;

            return await auditoriaContext.RegistrosAccesoDocumentoSensible.AnyAsync(r =>
                r.TenantId == tenantId
                && r.DocumentoId == registro.DocumentoId
                && r.TipoAcceso == registro.TipoAcceso
                && r.UsuarioId == registro.UsuarioId
                && r.ActorRealUsuarioId == registro.ActorRealUsuarioId
                && r.ViaAcceso == registro.ViaAcceso
                && r.ViaAccesoId == registro.ViaAccesoId
                && r.OcurridoEnUtc >= desdeUtc,
                cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Sin datos personales: ni el Documento ni el Usuario, y sin la excepción entera (su mensaje podría llevarlos);
            // basta el tipo para ver en el registro que la deduplicación está fallando y se escriben filas de más.
            logger?.LogWarning(
                "La comprobación de «ya registrado» del acceso Revisión IA falló ({TipoExcepcion}); se escribe la fila igualmente.",
                ex.GetType().Name);
            return false;
        }
    }
}
