namespace CaeManager.Application.Common;

/// <summary>
/// Marca una Query que devuelve <b>secretos del tenant en claro</b>: usuarios y
/// contraseñas de plataformas externas, tokens de integración — datos que el
/// sistema guarda cifrados en reposo y descifra solo para enseñárselos a quien
/// los va a usar.
///
/// <b>Por qué existe.</b> Una sesión de <c>SoporteLectura</c> ve el tenant
/// entero, y "el tenant entero" incluiría esto si nadie lo dijera. Pero una
/// contraseña del portal de un tercero no es un dato del cliente que se pueda
/// inspeccionar: es su <i>autoridad sobre otro sistema</i>. Leerla no es ver
/// datos, es quedarse con una llave — y una llave que sigue sirviendo después
/// de que la sesión de soporte se cierre, fuera de TALVEG, donde no llega
/// ninguna auditoría nuestra. Ni RLS lo detendría: son filas legítimas del
/// tenant visitado.
///
/// Es exactamente el escalón "¿recurso permitido?" entre "tenant permitido" y
/// "operación permitida": el privilegio de plataforma autoriza a <i>abrir</i>
/// el contexto del cliente, no a consultar cualquier cosa que haya dentro.
///
/// Quien la aplica es <see cref="AutorizacionSecretosDeTenantBehavior{TRequest,TResponse}"/>,
/// y quien vigila que ninguna Query nueva de credenciales se quede sin marcar
/// es un test de arquitectura.
/// </summary>
public interface IConsultaDeSecretosDeTenant;
