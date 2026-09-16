using CaeManager.Domain.Plataforma;

namespace CaeManager.Application.Plataforma;

/// <summary>
/// Una sesión privilegiada <b>viva ahora mismo</b>, ya revalidada. Que exista
/// este objeto significa que las tres condiciones se cumplen a la vez:
/// la sesión está abierta y dentro de su ventana, su concesión sigue vigente, y
/// esa concesión sigue cubriendo el tenant objetivo.
///
/// Los tres estados no pueden colapsarse en uno (ADR-011 § 8.1):
/// <code>
/// concesión existe  ≠  concesión válida ahora  ≠  sesión activa
/// </code>
/// La existencia de una <see cref="ConcesionPrivilegio"/> no otorga nada por sí
/// sola: quien tiene la capacidad todavía no la está ejerciendo. Y no basta con
/// mirar la ventana que la sesión lleva grabada — una concesión revocada tiene
/// que cortar las sesiones ya abiertas, y eso no se ve desde la fecha que la
/// sesión guardó al nacer.
/// </summary>
/// <param name="SesionId">La sesión, para auditar y para poder cerrarla.</param>
/// <param name="ConcesionId">La concesión que la ampara.</param>
/// <param name="TenantObjetivoId">El tenant cuyos datos abre. Uno concreto, nunca "todos".</param>
/// <param name="Capacidad">Qué permite hacer. Es lo que decide si puede escribir.</param>
/// <param name="UsuarioSimuladoId">A quién simula, solo bajo impersonación.</param>
public readonly record struct SesionPrivilegiadaActiva(
    Guid SesionId,
    Guid ConcesionId,
    Guid TenantObjetivoId,
    CapacidadPrivilegio Capacidad,
    Guid? UsuarioSimuladoId)
{
    /// <summary>
    /// Si esta capacidad permite escribir <b>en el modelo</b> — <c>BreakGlass</c>
    /// (acceso de emergencia) y <c>Aprovisionamiento</c> (PD-A3): la inspección
    /// de soporte y la administración de plataforma no tocan datos de un
    /// cliente, así que <c>SoporteLectura</c>, <c>AdminPlataforma</c> e
    /// <c>Impersonacion</c> quedan fuera.
    ///
    /// Esto es la capacidad en abstracto, no si hoy existe un camino que la
    /// ejecute — eso es <see cref="TieneCaminoDeEscritura"/>.
    /// </summary>
    public bool PermiteEscritura =>
        Capacidad is CapacidadPrivilegio.BreakGlass or CapacidadPrivilegio.Aprovisionamiento;

    /// <summary>
    /// Si esta sesión tiene, HOY, un camino de escritura realmente construido
    /// (ver <see cref="CapacidadesConCaminoDeEscritura"/>). <c>BreakGlass</c>
    /// permite escribir en el modelo pero no tiene camino todavía — por eso
    /// esta propiedad puede ser <c>false</c> mientras <see cref="PermiteEscritura"/>
    /// es <c>true</c>, nunca al revés.
    /// </summary>
    public bool TieneCaminoDeEscritura => CapacidadesConCaminoDeEscritura.Admite(Capacidad);
}
