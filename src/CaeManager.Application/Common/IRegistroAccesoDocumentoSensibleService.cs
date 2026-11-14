using CaeManager.Domain.Auditoria;

namespace CaeManager.Application.Common;

/// <summary>
/// DEC-36 (REC-099): registra el acceso efectivo al contenido de un
/// <see cref="Domain.Documentos.Documento"/> clasificado como sensible.
///
/// <para>
/// <b>Solo llamar desde los puntos de servicio que sirven contenido
/// clasificable a un actor humano</b> — los ocho medidos en HO-099-01 § 6,
/// de los cuales tres tienen clasificación
/// real: el archivo vigente y la versión anterior desde Auditoría (instancia
/// de <c>Documento</c>, clasificable por
/// <see cref="Domain.Documentos.TipoDocumento.Sensibilidad"/>), y la
/// evidencia de verificación de subcontrata (<c>TipoDocumentoId</c> propio,
/// sin ser un <c>Documento</c> — ver la segunda sobrecarga). Los otros cinco
/// sirven contenido sin ninguna clasificación (adjunto de correo, imagen de
/// firma/sello, plantilla en blanco) y no deben llamar a este servicio — ver el comentario de cada punto de
/// servicio y el veredicto completo en el RETURN PACKAGE de HO-099-01.
/// Posterior a HO-099-01: el zip de documentación de una Visita que el Gestor CAE
/// descarga para un Centro gestionado por correo (P1-X1,
/// <c>ObtenerPaqueteDocumentalVisitaQuery</c>) y el adjunto automático al crear la Visita
/// registran una apertura por cada Documento que contiene, en
/// <c>PaqueteDocumentalVisitaService.ConstruirAsync</c>. Y el «Preparar paquete» de Empresa 360
/// (<c>DescargarPaqueteEmpresaQuery</c>), que registra una apertura por cada Documento que entró en el zip antes de
/// entregarlo, todas en una sola transacción: si una falla, no queda ninguna y el zip no se entrega.
/// Y el documento que la pantalla Revisión IA carga sola (<c>GET /documentos/revision-ia/{revisionId}/archivo</c>), con
/// <see cref="TipoAccesoDocumentoSensible.RevisionIa"/>: cada acceso del visor de Revisión IA a un Documento sensible deja
/// una fila de tipo Revisión IA, igual que cada Apertura; cuenta para la señal de extracción masiva.
/// </para>
///
/// <para>
/// No es un interceptor de consultas —DEC-36 lo prohíbe expresamente— sino
/// una llamada explícita en capa de aplicación, con contexto suficiente para
/// saber quién accede, a qué Documento y bajo qué vía.
/// </para>
///
/// <para>
/// <b>Falla en cerrado.</b> Si el acceso sensible no queda registrado —también
/// el de Soporte TALVEG en una Sesión Privilegiada, que se escribe por la
/// función de base que vuelve a comprobar la sesión— lanza
/// <see cref="Auditoria.AccesoDocumentoSensibleNoRegistradoException"/>, y el
/// punto de servicio no entrega el contenido. Por eso se llama antes de
/// devolver el contenido, nunca después.
/// </para>
/// </summary>
public interface IRegistroAccesoDocumentoSensibleService
{
    /// <summary>
    /// Resuelve la categoría vigente del Documento y registra el acceso solo
    /// si es sensible (<c>Sensibilidad != SinDatosPersonales</c>) — nunca
    /// para documentos sin datos personales, DEC-36 lo prohíbe («nunca
    /// registrar de más»). Si el Documento ya no puede resolverse (baja
    /// física, caso raro fuera de la retención ordinaria — ver
    /// CalculadoraRetencionDocumento), se registra igual con la categoría
    /// más protectora: preferir un registro de más aquí antes que perder en
    /// silencio un acceso a lo que pudo haber sido un dato de salud (mismo
    /// criterio que el valor por defecto de <c>TipoDocumento.Sensibilidad</c>
    /// para un tipo no clasificado, REC-132).
    /// </summary>
    Task RegistrarSiSensibleAsync(
        Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default);

    /// <summary>
    /// Variante para contenido clasificado que <b>no</b> es una instancia de
    /// <see cref="Domain.Documentos.Documento"/> — hoy solo
    /// <c>VerificacionExternaSubcontrata</c> (ADR-005 § 2.3): tiene su propio
    /// <c>TipoDocumentoId</c> real (la evidencia adjunta no es una plantilla en
    /// blanco), así que la categoría se resuelve directamente sin el primer
    /// roundtrip de <see cref="RegistrarSiSensibleAsync(Guid, TipoAccesoDocumentoSensible, CancellationToken)"/>.
    /// <paramref name="recursoId"/> viaja en el campo <c>DocumentoId</c> del
    /// registro (sin FK, ver el comentario de la entidad) — referencia al
    /// recurso accedido, no necesariamente a un <c>Documento</c>.
    /// </summary>
    Task RegistrarSiSensibleAsync(
        Guid recursoId, Guid tipoDocumentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default);

    /// <summary>
    /// Abre un lote de aperturas para quien las agrupa en una transacción de varios guardados
    /// (<see cref="ITransaccionDeComando"/>). Registra igual que <see cref="RegistrarSiSensibleAsync(Guid, TipoAccesoDocumentoSensible, CancellationToken)"/>
    /// —mismo fallo en cerrado, misma auditoría—, pero la señal de extracción masiva (un contador en memoria que un rollback
    /// no deshace) no se emite hasta <see cref="ILoteAccesosSensibles.EmitirSenalesAsync"/>, que se llama solo tras confirmar.
    /// La transacción puede reintentar la operación: cada intento abre su propio lote y solo se emite el del intento
    /// confirmado. La implementación por defecto no difiere nada (emite al registrar, como el método suelto).
    /// </summary>
    ILoteAccesosSensibles IniciarLote() => new LoteSinDiferir(this);
}

/// <summary>Aperturas de un mismo paquete; ver <see cref="IRegistroAccesoDocumentoSensibleService.IniciarLote"/>.</summary>
public interface ILoteAccesosSensibles
{
    /// <summary>Registra la apertura (falla en cerrado) y retiene su señal de extracción masiva.</summary>
    Task RegistrarSiSensibleAsync(Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default);

    /// <summary>Emite las señales retenidas. Solo tras confirmar la transacción que contiene las aperturas.</summary>
    Task EmitirSenalesAsync(CancellationToken cancellationToken = default);
}

internal sealed class LoteSinDiferir(IRegistroAccesoDocumentoSensibleService servicio) : ILoteAccesosSensibles
{
    public Task RegistrarSiSensibleAsync(Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default) =>
        servicio.RegistrarSiSensibleAsync(documentoId, tipoAcceso, cancellationToken);

    public Task EmitirSenalesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
