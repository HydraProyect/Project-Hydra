using CaeManager.Application.Common;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Fake en memoria — nunca llama a Sentry de verdad (ver CODING_STANDARDS.md).
/// Modela el aislamiento por ámbito con una pila, igual que
/// <c>SentrySdk.PushScope</c>/<c>AddBreadcrumb</c> reales: una miga de pan
/// dejada dentro de un <see cref="IniciarAmbitoDeCaptura"/> solo aparece
/// adjunta a una excepción capturada DENTRO de ese mismo ámbito, nunca en el
/// de otro trabajo procesado antes o después — así un test puede comprobar
/// que el aislamiento funciona, no solo que se llamó al método.
/// </summary>
public class AlertaOperativaFalsa : IAlertaOperativa
{
    public List<(string Mensaje, NivelAlertaOperativa Nivel)> Alertas { get; } = [];

    public List<(Exception Excepcion, IReadOnlyList<string> MigasDePan)> ExcepcionesCapturadas { get; } = [];

    private readonly Stack<List<string>> _ambitos = new();

    public void Emitir(string mensaje, NivelAlertaOperativa nivel) => Alertas.Add((mensaje, nivel));

    public void CapturarExcepcion(Exception excepcion) =>
        ExcepcionesCapturadas.Add((excepcion, _ambitos.Count > 0 ? [.. _ambitos.Peek()] : []));

    public void DejarMigaDePan(string mensaje)
    {
        if (_ambitos.Count > 0)
            _ambitos.Peek().Add(mensaje);
    }

    public IDisposable IniciarAmbitoDeCaptura()
    {
        _ambitos.Push([]);
        return new Ambito(_ambitos);
    }

    private sealed class Ambito(Stack<List<string>> ambitos) : IDisposable
    {
        public void Dispose()
        {
            if (ambitos.Count > 0)
                ambitos.Pop();
        }
    }
}
