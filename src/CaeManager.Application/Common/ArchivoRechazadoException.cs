namespace CaeManager.Application.Common;

/// <summary>
/// Rechazo esperado de un archivo subido por el usuario (paquete ilegible, tope de tamaño, de filas o de celdas): una
/// denegación de entrada, no un error del sistema. El mensaje es genérico, nunca lleva el nombre ni el contenido del
/// archivo. <c>LoggingBehavior</c> lo registra como aviso sin la pila, para que un usuario que sube archivos malos no
/// inunde el log ni Sentry; la pantalla lo captura y muestra su propio texto.
/// </summary>
public class ArchivoRechazadoException : Exception
{
    public ArchivoRechazadoException(string message) : base(message) { }

    public ArchivoRechazadoException(string message, Exception innerException) : base(message, innerException) { }
}
