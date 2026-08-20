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
    /// Si esta sesión permite escribir. Solo <c>BreakGlass</c>: la inspección
    /// de soporte es de solo lectura sin excepción implícita, y administrar la
    /// plataforma no es tocar los datos de un cliente.
    /// </summary>
    public bool PermiteEscritura => Capacidad == CapacidadPrivilegio.BreakGlass;
}
