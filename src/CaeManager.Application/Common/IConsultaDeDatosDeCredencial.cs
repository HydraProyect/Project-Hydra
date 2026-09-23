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
/// <b>Diferencia con <see cref="IConsultaDeSecretosDeTenant"/>:</b> una Sesión
/// Privilegiada no se deniega aquí. El formulario guarda lo que esta consulta
/// precarga, así que un <c>null</c> por denegación a un rol que sí puede
/// guardar se convertiría en una credencial vacía al pulsar Guardar. A un rol
/// sin escritura no le pasa: no puede guardar.
/// </summary>
public interface IConsultaDeDatosDeCredencial;
