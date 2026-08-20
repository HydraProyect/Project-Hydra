namespace CaeManager.Application.Common;

/// <summary>
/// Identidad <b>de auditoría</b>: quién provocó realmente un cambio y por qué
/// vía. Es el contrato irrenunciable de la escisión del canal de identidad
/// (ADR-011 § 8.5, requisito 1).
///
/// Hasta ahora un único servicio servía a la vez para autorizar y para firmar
/// la auditoría. Con un solo carril, una impersonación no puede cumplir sus dos
/// exigencias a la vez —"ve exactamente lo que ve Juan" y "audita al
/// administrador real"—: o se registra al simulado, que es mentira, o se
/// registra al administrador y entonces la sesión no es distinguible de un
/// acceso directo suyo.
/// </summary>
/// <param name="ActorRealUsuarioId">
/// Quien está detrás del teclado. Nunca se sustituye por el usuario simulado,
/// ni siquiera durante una impersonación: ese es justamente el punto.
/// </param>
/// <param name="UsuarioSimuladoId">
/// A quién se está simulando, o <c>null</c> si no hay impersonación. Reservado
/// hasta que exista la capacidad (ADR-011 § 8.2).
/// </param>
/// <param name="Via">Desde dónde se opera.</param>
/// <param name="ViaAccesoId">
/// El Id de la fila que ampara la vía —la <c>AsignacionOperacion</c> o la
/// sesión privilegiada—, o <c>null</c> en la vía normal.
/// </param>
public readonly record struct ActorAuditoria(
    Guid? ActorRealUsuarioId,
    Guid? UsuarioSimuladoId,
    TipoViaAcceso Via,
    Guid? ViaAccesoId)
{
    /// <summary>
    /// Sin identidad resuelta: jobs de fondo, seeders y el guardado síncrono
    /// que no puede esperar a los claims. Se marca la vía como desconocida en
    /// vez de asumir la normal.
    /// </summary>
    public static ActorAuditoria SinResolver => new(null, null, TipoViaAcceso.Desconocida, null);

    /// <summary>Un usuario operando su propio tenant.</summary>
    public static ActorAuditoria Normal(Guid? usuarioId) => new(usuarioId, null, TipoViaAcceso.Normal, null);
}
