namespace CaeManager.Application.Visitas.PaqueteDocumental;

/// <summary>
/// Genera un zip con la documentación vigente de la Empresa y de los
/// Trabajadores de una Visita, y lo deja adjunto en un mensaje saliente
/// automático dentro de la conversación de correo que originó la solicitud
/// — cierra "cuando se programe una visita que no es por plataforma sino
/// por correo se cree un ticket que adjunte automáticamente en un zip los
/// documentos" del pedido del usuario. El "ticket" es el propio hilo de
/// <c>Conversacion</c>, ya el registro auditable de la gestión — no se
/// introduce una entidad Ticket nueva para esto (YAGNI): <c>Visita.Origen</c>
/// ya deja constancia de que la visita nació de un correo, y este mensaje
/// automático es la respuesta operativa dentro de ese mismo hilo.
///
/// <para>
/// El zip sale hacia el Cliente empresarial, así que solo lleva documentos
/// <b>vigentes</b> y <b>uno por titular y tipo</b> (el de mayor vigencia): ni vencidos
/// ni copias repetidas. Un tipo del que solo hay copias vencidas no se envía y
/// queda registrado en el log. Detalle en <c>PaqueteDocumentalVisitaService</c>.
/// </para>
/// </summary>
public interface IPaqueteDocumentalVisitaService
{
    Task GenerarYEnviarAsync(Guid visitaId, Guid conversacionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Construye el zip con la misma selección que <see cref="GenerarYEnviarAsync"/>
    /// (vigentes, uno por titular y tipo, nunca vencidos) sin adjuntarlo a ninguna
    /// conversación. Lo usa la descarga manual de un Centro gestionado por correo
    /// (P1-X1). <b>No autoriza</b>: quien lo llame comprueba antes Tenant y alcance, y
    /// registra el acceso a cada documento de <see cref="PaqueteDocumentalZip.Documentos"/>
    /// que sea sensible. Null si no hay nada que empaquetar o el Centro no requiere
    /// gestión CAE.
    /// </summary>
    Task<PaqueteDocumentalZip?> ConstruirAsync(Guid visitaId, CancellationToken cancellationToken = default);
}

/// <summary>Un documento que entró de verdad en el zip (su archivo se pudo abrir).</summary>
public record DocumentoEnPaquete(Guid DocumentoId, Guid TipoDocumentoId);

public record PaqueteDocumentalZip(
    string NombreArchivo,
    byte[] Contenido,
    IReadOnlyList<DocumentoEnPaquete> Documentos,
    string CentroNombre,
    DateOnly FechaInicio,
    DateOnly FechaFin);
