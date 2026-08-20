namespace CaeManager.Application.Common;

/// <summary>
/// Por qué vía estaba operando quien provocó un cambio. Se guarda en cada
/// registro de auditoría junto a la identidad, porque saber <b>quién</b> tocó
/// un dato sin saber <b>desde dónde</b> deja media pregunta sin responder: no
/// es lo mismo que el gestor de la casa edite un documento a que lo edite una
/// consultora operando el workspace por delegación.
///
/// Ver ADR-011 § 8.5 (requisitos 1 y 2 del plano de privilegio de plataforma).
/// </summary>
public enum TipoViaAcceso
{
    /// <summary>
    /// El usuario opera su propio tenant. Es el caso de la inmensa mayoría, y
    /// el que se asume para las filas históricas anteriores a esta columna.
    /// </summary>
    Normal = 0,

    /// <summary>
    /// El usuario opera un tenant ajeno a través de una
    /// <c>AsignacionOperacion</c> (plano 2). <c>ViaAccesoId</c> lleva el Id de
    /// esa operación, que es lo que permite responder "esto se hizo bajo la
    /// delegación X" sin cruzar tablas a mano.
    /// </summary>
    OperacionDelegada = 1,

    /// <summary>
    /// Acceso privilegiado de plataforma (soporte, impersonación,
    /// break-glass). Reservado: no se emite hasta que exista
    /// <c>SesionPrivilegiada</c> — ver ADR-011 § 8.
    /// </summary>
    SesionPrivilegiada = 2,

    /// <summary>
    /// No se pudo resolver la vía. Existe para que el agujero sea
    /// <b>visible</b> en vez de silencioso: hoy solo lo produce el guardado
    /// síncrono cuando los claims aún no están resueltos y bloquear
    /// arriesgaría un interbloqueo del circuito. Una fila así dice "no lo sé",
    /// que es honesto; lo que no puede hacer es disfrazarse de
    /// <see cref="Normal"/>.
    /// </summary>
    Desconocida = 3
}
