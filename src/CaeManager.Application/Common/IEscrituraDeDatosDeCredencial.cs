namespace CaeManager.Application.Common;

/// <summary>
/// Marca un Command que <b>crea, cambia o borra datos de una credencial</b> de
/// acceso a una plataforma CAE del Tenant propietario (usuario o contraseña).
///
/// Exige la autenticación en dos pasos activa en la cuenta real que lo envía,
/// igual que leer esos datos (<see cref="IConsultaDeSecretosDeTenant"/>,
/// <see cref="IConsultaDeDatosDeCredencial"/>): quien puede fijar la llave de una
/// plataforma de terceros puede también sustituirla por una que conozca, y eso
/// es tanta autoridad sobre ese sistema como leerla. Lo aplica
/// <see cref="AutorizacionSecretosDeTenantBehavior{TRequest,TResponse}"/>, que
/// lanza <see cref="SegundoFactorRequeridoParaCredencialesException"/> para que la
/// pantalla lleve al usuario a activarla (hueco declarado en #900, cerrado en P1-I2).
///
/// El rol y la Sesión Privilegiada no se vuelven a comprobar aquí: un Command ya
/// los ha pasado por <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>,
/// que corre antes y deniega a los roles sin escritura y a toda sesión de
/// plataforma sin camino de escritura para ese Command.
///
/// <see cref="EscribeDatosDeCredencial"/> existe para los Commands que solo a
/// veces tocan la credencial (crear un canal de gestión sin usuario ni
/// contraseña, o editarlo sin cambiarlas): en esos casos no hay nada que
/// proteger y el 2FA no se exige.
/// </summary>
public interface IEscrituraDeDatosDeCredencial
{
    bool EscribeDatosDeCredencial => true;
}
