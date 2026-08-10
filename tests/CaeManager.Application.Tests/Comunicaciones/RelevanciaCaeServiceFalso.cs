using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Tests.Comunicaciones;

public class RelevanciaCaeServiceFalso : IRelevanciaCaeService
{
    public List<Guid> Llamadas { get; } = [];

    public Task ProcesarAsync(Conversacion conversacion, CancellationToken cancellationToken = default)
    {
        Llamadas.Add(conversacion.Id);
        return Task.CompletedTask;
    }
}
