using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Tests;

/// <summary>
/// Registro de pruebas para ASVS 4.0.3 V7.1.1: captura TODAS las entradas, de cualquier nivel, con el texto
/// formateado, los valores estructurados (lo que un proveedor JSON escribiría) y la excepción entera. Una
/// afirmación hecha solo sobre el nivel esperado dejaría pasar una entrada de otro nivel que filtre un dato.
/// </summary>
public sealed class RegistroQueCapturaTodo<T> : ILogger<T>
{
    private readonly object _cerrojo = new();
    private readonly List<EntradaDeRegistro> _entradas = [];

    public IReadOnlyList<EntradaDeRegistro> Entradas
    {
        get { lock (_cerrojo) return _entradas.ToList(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var estructurados = state is IEnumerable<KeyValuePair<string, object?>> pares
            ? string.Join(" ", pares.Select(p => $"{p.Key}={p.Value}"))
            : string.Empty;
        lock (_cerrojo)
            _entradas.Add(new EntradaDeRegistro(logLevel, formatter(state, exception) + " " + estructurados + " " + exception, exception));
    }

    /// <summary>
    /// Falla si alguna entrada, de cualquier nivel, lleva una excepción o contiene alguno de los textos
    /// prohibidos. Exige al menos una entrada: un registro vacío no demuestra nada.
    /// </summary>
    public void DebeEstarLimpioDeDatosPersonales(params string[] prohibidos)
    {
        var entradas = Entradas;
        entradas.Should().NotBeEmpty("sin ninguna entrada capturada la prueba no observaría nada");
        foreach (var entrada in entradas)
        {
            entrada.Excepcion.Should().BeNull($"la excepción entera lleva su mensaje y el de su causa (entrada de nivel {entrada.Nivel})");
            entrada.Contenido.Should().NotContainAny(prohibidos, $"una entrada de nivel {entrada.Nivel} no puede llevar datos personales");
        }
    }
}

public sealed record EntradaDeRegistro(LogLevel Nivel, string Contenido, Exception? Excepcion);
