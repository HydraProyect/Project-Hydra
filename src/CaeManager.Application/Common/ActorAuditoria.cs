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
    ///
    /// <para>
    /// "Sin identidad" no equivale a "sin saber quién actuaba": un servicio de
    /// fondo llega aquí y aun así su tipo de actor es <c>Sistema</c>, porque lo
    /// declara con <see cref="AmbitoActorAuditoria.EstablecerSistema"/>. Los dos
    /// ejes son independientes — ver <see cref="ResolverTipoActor"/>.
    /// </para>
    /// </summary>
    public static ActorAuditoria SinResolver => new(null, null, TipoViaAcceso.Desconocida, null);

    /// <summary>Un usuario operando su propio tenant.</summary>
    public static ActorAuditoria Normal(Guid? usuarioId) => new(usuarioId, null, TipoViaAcceso.Normal, null);

    /// <summary>
    /// Qué <b>clase</b> de actor es este — eje ortogonal a <see cref="Via"/>, y
    /// la única regla del sistema que lo decide (P41c). Es un método y no una
    /// propiedad a propósito: consulta el ámbito ambiental, así que su resultado
    /// depende de dónde se llame, y una propiedad lo haría parecer un dato
    /// guardado en la estructura.
    ///
    /// <para>
    /// El ámbito declarado manda sobre la identidad resuelta, y ese orden no es
    /// arbitrario: una llamada a la API pública con <c>ClaveApi</c> llega con
    /// identidad resuelta —el handler mete el Id de la clave como
    /// <c>NameIdentifier</c>— y sin este orden pasaría por
    /// <see cref="TipoActor.Persona"/>, que es exactamente la confusión
    /// que este eje existe para deshacer.
    /// </para>
    ///
    /// <para>
    /// Sin ámbito declarado y sin identidad resuelta queda
    /// <see cref="TipoActor.Desconocido"/>, no <c>Sistema</c>: el
    /// guardado síncrono con claims sin resolver es una <b>persona</b> a la que
    /// no se pudo identificar, y llamarla máquina sería una afirmación falsa
    /// sobre un rastro de auditoría.
    /// </para>
    /// </summary>
    public TipoActor ResolverTipoActor() =>
        AmbitoActorAuditoria.TipoActorActual
        ?? (ActorRealUsuarioId is not null ? TipoActor.Persona : TipoActor.Desconocido);
}
