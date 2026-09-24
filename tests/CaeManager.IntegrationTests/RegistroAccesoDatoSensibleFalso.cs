using CaeManager.Application.Common;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Para los tests que miden otra cosa (el SQL de una proyección) y no la
/// auditoría: anota la lectura sin escribir en la base.
/// </summary>
public class RegistroAccesoDatoSensibleFalso : IRegistroAccesoDatoSensibleService
{
    public List<(string EntidadTipo, Guid EntidadId)> Registrados { get; } = [];

    public Task RegistrarAsync(string entidadTipo, Guid entidadId, CancellationToken cancellationToken = default)
    {
        Registrados.Add((entidadTipo, entidadId));
        return Task.CompletedTask;
    }
}
