using CaeManager.Domain.Common;

namespace CaeManager.Domain.Auditoria;

/// <summary>
/// Rastro del acceso <b>efectivo al contenido</b> de un Documento clasificado
/// como sensible — DEC-36 (REC-099, acta en
/// <c>decisiones/DEC-33-36-lote-D-2026-09-02.md</c> del repositorio de
/// negocio): «registrar la apertura/acceso efectivo al contenido de
/// documentos clasificados como sensibles [...], nunca cada query EF ni cada
/// aparición de una fila en una lista». Antes de esta entidad, un Gestor CAE
/// que abría un reconocimiento médico no quedaba registrado en ningún sitio
/// (DCR-08, Issue #12) — solo el soporte de plataforma dejaba rastro
/// (<see cref="Soporte.RegistroActividadSoporte"/>).
///
/// <para>
/// <b>Append-only a propósito.</b> Sin método de modificación ni de borrado:
/// un rastro que se puede editar o eliminar por vía ordinaria no es un
/// rastro (DEC-36, «no permitir modificación ni borrado ordinario»). RLS
/// obligatoria en la migración que crea la tabla (ver
/// <c>HabilitarRlsRegistrosAccesoDocumentoSensible</c>).
/// </para>
///
/// <para>
/// <b>Qué NO se registra aquí, deliberadamente</b> (HO-099-01 § 6-7, no un
/// olvido): listados, contadores o búsquedas que solo devuelvan metadatos —
/// DEC-36 lo prohíbe en literal («nunca cada query EF ni cada aparición de
/// una fila en una lista»), y hacerlo volvería el volumen inmanejable sin
/// aportar accountability real. Tampoco los <b>once</b> puntos de proceso
/// automático que también pasan por
/// <c>IFileStorageService.AbrirAsync</c> (verificación IA, validación
/// oficial, detección de trabajadores, firma en campo, generación desde
/// plantilla, paquete documental de visita, actualización desde adjunto):
/// ahí no hay una persona mirando, y DEC-36 habla de acceso efectivo por un
/// actor — el veredicto completo sobre esos once va en el RETURN PACKAGE de
/// HO-099-01, no en este comentario. Y tampoco los puntos que sirven
/// <c>IFileStorageService.AbrirAsync</c> sin ninguna clasificación real —
/// adjuntos de correo (<c>AdjuntoMensaje</c>), firma/sello de imagen
/// (<c>FirmaGuardadaUsuario</c>/<c>SelloEmpresa</c>) y las dos plantillas en
/// blanco (<c>TipoDocumentoCentro.ArchivoUrl</c>,
/// <c>PlantillaDocumentoVersion.ArchivoOriginalUrl</c>): ninguno tiene un
/// <c>TipoDocumentoId</c> real (o, en el caso de las plantillas, el contenido
/// está deliberadamente vacío), así que la categoría de DEC-36 no tiene de
/// dónde salir sin inventar una fuente distinta al punto único de REC-132 —
/// justo lo que esa decisión prohíbe. La evidencia de
/// <c>VerificacionExternaSubcontrata</c> SÍ se registra (tiene su propio
/// <c>TipoDocumentoId</c>, y el archivo adjunto no es una plantilla en
/// blanco) pese a no ser una instancia de <see cref="Documentos.Documento"/>
/// — ver la sobrecarga de <c>IRegistroAccesoDocumentoSensibleService</c> que
/// acepta el <c>TipoDocumentoId</c> directamente.
/// </para>
/// </summary>
public class RegistroAccesoDocumentoSensible : EntidadConTenant
{
    /// <summary>
    /// El <see cref="Documentos.Documento"/> accedido. Guid suelto, sin FK ni
    /// navegación — mismo criterio que <see cref="RegistroAuditoria.EntidadId"/>:
    /// el rastro debe sobrevivir a la baja del Documento que describe.
    /// </summary>
    public Guid DocumentoId { get; private set; }

    /// <summary>
    /// Categoría en el momento del acceso, copiada de
    /// <see cref="Documentos.TipoDocumento.Sensibilidad"/> — el punto único de
    /// consulta de REC-132. Se congela aquí a propósito: si el tipo se
    /// reclasifica más adelante, el rastro sigue diciendo bajo qué categoría
    /// se autorizó y se vio el acceso en su momento, no la categoría actual.
    /// </summary>
    public Documentos.SensibilidadDocumental Sensibilidad { get; private set; }

    public TipoAccesoDocumentoSensible TipoAcceso { get; private set; }

    /// <summary>
    /// Quien figura como autor del acceso — igual criterio dual que
    /// <see cref="RegistroAuditoria"/> (ADR-011 § 8.4/8.5): durante una
    /// impersonación sería el usuario simulado.
    /// </summary>
    public Guid? UsuarioId { get; private set; }

    /// <summary>Quien estaba realmente detrás del teclado. Ver <see cref="RegistroAuditoria.ActorRealUsuarioId"/>.</summary>
    public Guid? ActorRealUsuarioId { get; private set; }

    /// <summary>
    /// «Contexto operativo» de DEC-36: desde dónde se operaba —el propio
    /// tenant, un Context Workspace por operación delegada, o una sesión
    /// privilegiada—. Mismo enum que <see cref="RegistroAuditoria.ViaAcceso"/>,
    /// reutilizado a propósito en vez de inventar un segundo vocabulario para
    /// la misma pregunta.
    /// </summary>
    public TipoViaAccesoAuditoria ViaAcceso { get; private set; }

    /// <summary>La fila que ampara la vía — la operación delegada o la sesión privilegiada.</summary>
    public Guid? ViaAccesoId { get; private set; }

    public DateTime OcurridoEnUtc { get; private set; }

    /// <summary>
    /// «Si el acceso fue privilegiado», tal cual lo pide DEC-36 — atajo de
    /// lectura sin columna propia (mismo patrón que
    /// <see cref="Documentos.TipoDocumento.RevelaSalud"/>): no se traduce a
    /// SQL, comparar <c>ViaAcceso</c> directamente dentro de un
    /// <c>Where</c>/<c>Select</c> sobre el <c>DbSet</c>.
    /// </summary>
    public bool EsPrivilegiado => ViaAcceso == TipoViaAccesoAuditoria.SesionPrivilegiada;

    private RegistroAccesoDocumentoSensible()
    {
    }

    public RegistroAccesoDocumentoSensible(
        Guid documentoId,
        Documentos.SensibilidadDocumental sensibilidad,
        TipoAccesoDocumentoSensible tipoAcceso,
        Guid? usuarioId,
        Guid? actorRealUsuarioId,
        TipoViaAccesoAuditoria viaAcceso,
        Guid? viaAccesoId)
    {
        if (documentoId == Guid.Empty)
            throw new ArgumentException("El registro de acceso debe identificar el Documento accedido.", nameof(documentoId));

        DocumentoId = documentoId;
        Sensibilidad = sensibilidad;
        TipoAcceso = tipoAcceso;
        UsuarioId = usuarioId;
        ActorRealUsuarioId = actorRealUsuarioId;
        ViaAcceso = viaAcceso;
        ViaAccesoId = viaAccesoId;
        OcurridoEnUtc = DateTime.UtcNow;
    }
}
