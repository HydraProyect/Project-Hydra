using CaeManager.Application.Common;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>Guarda las filas pedidas y, si se le indica, falla como una base que no admite la escritura.</summary>
public sealed class RegistroExportacionFalso : IRegistroExportacionService
{
    public List<(string EntidadTipo, int Filas, IReadOnlyDictionary<string, string> Criterios)> Filas { get; } = [];

    public bool Falla { get; init; }

    /// <summary>Se ejecuta al registrar: permite comprobar qué había ocurrido ya en ese momento.</summary>
    public Action? AlRegistrar { get; init; }

    public Task RegistrarAsync(
        string entidadTipo, int filas, IReadOnlyDictionary<string, string> criterios,
        CancellationToken cancellationToken = default)
    {
        if (Falla)
            throw new InvalidOperationException("Registro no disponible.");

        AlRegistrar?.Invoke();
        Filas.Add((entidadTipo, filas, criterios));
        return Task.CompletedTask;
    }
}
