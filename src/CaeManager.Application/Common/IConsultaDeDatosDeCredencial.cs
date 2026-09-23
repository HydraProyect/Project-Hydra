namespace CaeManager.Application.Common;

/// <summary>
/// Marca una Query que descifra <b>datos de una credencial sin su contraseña</b>
/// —el usuario de un acceso a portal, cifrado en reposo— para
/// precargar el formulario que la edita.
///
/// Solo la leen los roles con escritura, igual que
/// <see cref="IConsultaDeSecretosDeTenant"/> (decisión del propietario,
/// 2026-09-23): el rol Consulta —propio o delegado por un Operador CAE
/// externo— recibe <c>null</c>. Lo aplica
/// <see cref="AutorizacionSecretosDeTenantBehavior{TRequest,TResponse}"/>.
///
/// Tampoco se entrega en una Sesión Privilegiada (opción D, 2026-09-23): toda
/// lectura efectiva queda en la auditoría, y en esa sesión la conexión adopta
/// <c>cae_app_soporte</c>, que no puede escribir la fila — sin rastro no hay
/// dato. El <c>null</c> no provoca un read-modify-write destructivo: el
/// formulario guarda lo que esta consulta precarga, pero en una Sesión
/// Privilegiada los comandos que guardan una credencial se deniegan
/// (<see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>), igual que
/// para un rol sin escritura.
/// </summary>
public interface IConsultaDeDatosDeCredencial;
