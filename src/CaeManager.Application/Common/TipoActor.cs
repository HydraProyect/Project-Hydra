namespace CaeManager.Application.Common;

/// <summary>
/// Qué <b>clase</b> de actor provocó un cambio auditado. Eje ortogonal a
/// <see cref="TipoViaAcceso"/>: la vía dice desde dónde se operaba, este dice
/// quién es la clase de actor que operaba. Ver
/// <c>CaeManager.Domain.Auditoria.TipoActorAuditoria</c>, su espejo en Domain,
/// para el razonamiento completo y para la decisión del propietario que lo
/// introduce (2026-09-19, P41c).
///
/// <para>
/// Los valores y sus números coinciden con el espejo de Domain a propósito —el
/// interceptor convierte de uno a otro con un cast, mismo patrón que
/// <see cref="TipoViaAcceso"/>—, pero lo que se persiste es el <b>nombre</b>,
/// no el número.
/// </para>
/// </summary>
public enum TipoActor
{
    /// <summary>No se pudo determinar. El único valor que no afirma ni persona ni máquina.</summary>
    Desconocido = 0,

    /// <summary>Un usuario autenticado.</summary>
    Persona = 1,

    /// <summary>La propia plataforma: hosted services, barridos, colas, siembra.</summary>
    Sistema = 2,

    /// <summary>Un sistema de un tercero: la API pública con <c>ClaveApi</c> o un webhook de proveedor. No una persona sin sesión.</summary>
    IntegracionExterna = 3
}
