using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Registro que conserva cada entrada con todo lo que un sumidero real escribiría: el mensaje ya formateado
/// y, si la hay, la excepción entera (tipo, mensaje y traza). Sirve para comprobar que un aviso no filtra
/// la clave de un fichero del almacenamiento ni por el mensaje ni por la excepción.
/// </summary>
public sealed class RegistroDeAvisos<T> : ILogger<T>
{
    public List<(LogLevel Nivel, string Texto)> Entradas { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entradas.Add((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " " + exception)));
}
