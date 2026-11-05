namespace CaeManager.Application.Common;

/// <summary>
/// Una lectura bajo Sesión Privilegiada cuya fila de <c>RegistrosActividadSoporte</c> no se
/// pudo escribir (issue #2001). Se lanza antes de que corra el handler: lo que no deja rastro
/// no se sirve. También cuando el circuito aún nombra una Sesión Privilegiada que ya no
/// resuelve: la conexión seguiría siendo de soporte. El motivo concreto de un rechazo de la
/// función queda en el registro de la aplicación.
/// </summary>
public sealed class LecturaPrivilegiadaNoRegistradaException(string operacion)
    : InvalidOperationException(
        $"No se pudo registrar la lectura «{operacion}» de la Sesión Privilegiada; los datos no se sirven.")
{
    public string Operacion { get; } = operacion;
}
