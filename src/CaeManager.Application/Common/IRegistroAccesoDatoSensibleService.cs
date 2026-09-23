namespace CaeManager.Application.Common;

/// <summary>
/// Deja rastro en la auditoría de cada lectura efectiva de un dato de credencial
/// cifrado en reposo (usuario o contraseña de una plataforma externa):
/// ARCHITECTURE.md § Datos sensibles y decisión del propietario del 2026-09-23
/// (P1, opción D) — «auditar cada lectura sin registrar secretos, separando
/// Actor real, Usuario simulado, Tenant propietario y objeto leído».
///
/// <para>
/// Lo llaman los handlers de las Queries marcadas con
/// <see cref="IConsultaDeSecretosDeTenant"/> o <see cref="IConsultaDeDatosDeCredencial"/>,
/// <b>solo cuando hay algo que devolver</b> y <b>antes de devolverlo</b>: si el
/// registro no se puede guardar, la excepción sube y el dato no se entrega
/// (fallo cerrado). Un test de arquitectura exige que todo handler de esas
/// Queries reciba este servicio.
/// </para>
///
/// <para>
/// La fila va al Tenant activo —el Tenant propietario del dato, también en un
/// Context Workspace delegado— y lleva el actor dual de
/// <see cref="IActorAuditoria"/>: quien estaba realmente detrás del teclado y,
/// si hubiera impersonación, el usuario simulado. Hoy no la hay en este camino:
/// la impersonación es una Sesión Privilegiada y
/// <see cref="AutorizacionSecretosDeTenantBehavior{TRequest,TResponse}"/> deniega
/// antes estas consultas; la separación queda por si eso cambia.
/// </para>
/// </summary>
public interface IRegistroAccesoDatoSensibleService
{
    /// <param name="entidadTipo">Nombre simple de la entidad que guarda el dato, igual que en el resto de la auditoría.</param>
    /// <param name="entidadId">Id de la fila leída, para que la lectura aparezca junto a sus altas y cambios.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    Task RegistrarAsync(string entidadTipo, Guid entidadId, CancellationToken cancellationToken = default);
}
