namespace CaeManager.Domain.Auditoria;

/// <summary>
/// Qué <b>clase</b> de actor provocó un cambio auditado. Es el espejo en Domain
/// de <c>TipoActorAuditoria</c> de Application: se duplica en vez de
/// compartirse por el mismo motivo que <see cref="TipoViaAccesoAuditoria"/> —
/// Domain no referencia Application.
///
/// <para>
/// Eje <b>ortogonal</b> a <see cref="TipoViaAccesoAuditoria"/>, y no se
/// confunden: la vía responde "¿desde dónde se operaba?" (el propio tenant, una
/// operación delegada, una sesión privilegiada) y este eje responde "¿qué clase
/// de actor operaba?" (una persona, la propia plataforma, un tercero con clave).
/// Antes de este eje las dos preguntas se contestaban con la misma columna, y
/// <c>Desconocida</c> cargaba con dos significados incompatibles: el barrido de
/// retención y una persona cuyos claims no se resolvieron a tiempo producían
/// filas idénticas.
/// </para>
///
/// <para>
/// Decisión del propietario del producto (2026-09-19, P41c): la auditoría
/// conserva el tipo de actor <b>sin distinción visual todavía</b>, para poder
/// mostrarlo más adelante sin migración. Ninguna pantalla lo lee hoy.
/// </para>
/// </summary>
public enum TipoActorAuditoria
{
    /// <summary>
    /// No se pudo determinar qué clase de actor operaba. Es el valor de las
    /// filas anteriores a este eje —para las que no se infiere nada
    /// retroactivamente— y el del guardado síncrono cuya identidad no estaba
    /// resuelta. Existe para que el hueco se vea: una fila así dice "no lo sé",
    /// que es honesto, y es el único valor que no afirma ni persona ni máquina.
    /// </summary>
    Desconocido = 0,

    /// <summary>
    /// Un usuario autenticado. No dice <b>quién</b> —para eso están
    /// <c>UsuarioId</c> y <c>ActorRealUsuarioId</c>—, solo que detrás del acto
    /// había una persona.
    /// </summary>
    Persona = 1,

    /// <summary>
    /// La propia plataforma actuando por su cuenta: hosted services, barridos,
    /// colas, expiraciones, siembra. No hay nadie a quien atribuir el acto, y
    /// eso es distinto de no saberlo.
    /// </summary>
    Sistema = 2,

    /// <summary>
    /// Un tercero llamando a la API pública con una <c>ClaveApi</c>. No es una
    /// persona autenticada ni un proceso propio: es una organización externa
    /// con una credencial, y la auditoría del tenant tiene derecho a
    /// distinguirlo de su propio personal.
    /// </summary>
    IntegracionExterna = 3
}
