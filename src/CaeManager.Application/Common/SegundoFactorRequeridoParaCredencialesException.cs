namespace CaeManager.Application.Common;

/// <summary>
/// La lanza <see cref="AutorizacionSecretosDeTenantBehavior{TRequest,TResponse}"/>
/// cuando un usuario cuyo rol sí lee datos de credencial del Tenant propietario
/// pide uno sin tener la autenticación en dos pasos activa (P1-I1), o cuando
/// envía un Command que los escribe (<see cref="IEscrituraDeDatosDeCredencial"/>, P1-I2).
///
/// <para>
/// Es la única denegación de ese behavior que no se disfraza de "no hay
/// credencial guardada" (<c>null</c>): la pantalla tiene que distinguirla para
/// llevar al usuario a configurar el 2FA, y distinguirla no filtra nada del
/// recurso, porque depende solo de la cuenta que pregunta, nunca de si el
/// Tenant propietario tiene o no credenciales configuradas. Por eso el behavior
/// la lanza después de comprobar la Sesión Privilegiada y el rol, nunca antes:
/// un rol que no lee secretos recibe <c>null</c> igual que siempre, sin que se le
/// invite a activar nada.
/// </para>
/// </summary>
public sealed class SegundoFactorRequeridoParaCredencialesException()
    : Exception("Leer o escribir datos de credencial del Tenant propietario exige tener activa la autenticación en dos pasos.");
