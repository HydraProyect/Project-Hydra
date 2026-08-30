namespace CaeManager.Application.Common;

/// <summary>
/// ¿Puede el actor <b>modificar</b> las asignaciones de un Centro? Es un eje
/// distinto de <see cref="IAlcanceDatosService"/>, y la distinción es la razón
/// de que este servicio exista en vez de reutilizar aquel.
///
/// <para>
/// <b>«Es visible» no implica «puede modificar»</b> (decisión del propietario,
/// 2026-08-29). El caso que lo demuestra sin ambigüedad es el rol
/// <c>Consulta</c>: <see cref="IAlcanceDatosService.TieneAccesoTotalAsync"/>
/// le devuelve <c>true</c> —ve toda la organización— y no debe poder tocar
/// una sola asignación. Derivar la autoridad de la visibilidad habría
/// convertido a un rol de solo lectura en administrador de carteras. Que hoy
/// <c>AutorizacionEscrituraBehavior</c> lo pare antes por lista blanca no
/// cambia el diseño: esa es <i>otra</i> comprobación, en otra capa, y
/// apoyarse en ella para no hacer esta es exactamente el acoplamiento que la
/// regla prohíbe.
/// </para>
///
/// <para>
/// <b>Tampoco es «la asignación es mía».</b> Reducir la autoridad a
/// <c>Asignacion.UsuarioId == UsuarioActual</c> dejaría fuera al
/// <c>CoordinadorCae</c>, que tiene autoridad sobre las asignaciones de los
/// gestores a su cargo. La regla es:
/// <code>actor → rol → ámbito bajo su autoridad → asignación objetivo</code>
/// </para>
///
/// <para>
/// El ámbito se deriva de la misma cartera operativa que alimenta el alcance
/// de lectura —no hay una segunda fuente de verdad que mantener sincronizada—
/// pero <b>solo para los roles con capacidad administrativa sobre él</b>. Para
/// los demás la respuesta es <c>false</c> aunque lo vean todo.
/// </para>
/// </summary>
public interface IAutoridadAsignacionesService
{
    /// <summary>
    /// True si el actor puede crear o dar de baja asignaciones en
    /// <paramref name="centroId"/>. Falso también cuando el centro no existe o
    /// no es del tenant: no se distingue «no existe» de «no es tuyo», mismo
    /// criterio que las consultas <c>*PorId*</c> — decir cuál de las dos es
    /// confirmar la existencia de un centro ajeno.
    /// </summary>
    Task<bool> PuedeModificarAsignacionesDelCentroAsync(
        Guid centroId, CancellationToken cancellationToken = default);

    /// <summary>
    /// El subconjunto de <paramref name="centroIds"/> sobre el que el actor
    /// tiene autoridad. Para los lotes: resuelve el ámbito una sola vez en vez
    /// de una consulta por centro, y deja que el llamador decida qué hacer con
    /// los que quedan fuera (rechazar el lote entero o informar de cuántos se
    /// omitieron, según lo que ya haga ese comando con los inválidos).
    /// </summary>
    Task<IReadOnlyList<Guid>> FiltrarCentrosConAutoridadAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// True si el actor puede dar de alta o baja asignaciones de
    /// <paramref name="trabajadorId"/> — auditoría Módulo 5, hallazgo crítico
    /// 6/9. Antes solo se comprobaba que el trabajador existiera en el
    /// tenant: un trabajador fuera de la cartera del actor, pero visible por
    /// GUID, quedaba "secuestrado" hacia esa cartera en cuanto se le asignaba,
    /// exponiendo DNI, contacto y documentación médica. Mismo criterio de "no
    /// existe" vs. "no es tuyo" que <see cref="PuedeModificarAsignacionesDelCentroAsync"/>.
    /// </summary>
    Task<bool> PuedeModificarAsignacionesDelTrabajadorAsync(
        Guid trabajadorId, CancellationToken cancellationToken = default);

    /// <summary>El subconjunto de <paramref name="trabajadorIds"/> sobre el que el actor tiene autoridad — para los lotes.</summary>
    Task<IReadOnlyList<Guid>> FiltrarTrabajadoresConAutoridadAsync(
        IReadOnlyList<Guid> trabajadorIds, CancellationToken cancellationToken = default);
}
