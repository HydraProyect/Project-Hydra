using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Tests.Comunicaciones;

public class IntencionConversacionServiceFalso : IIntencionConversacionService
{
    public List<Guid> ConversacionesProcesadas { get; } = [];
    public Task ProcesarAsync(Conversacion conversacion, CancellationToken cancellationToken = default)
    {
        ConversacionesProcesadas.Add(conversacion.Id);
        return Task.CompletedTask;
    }
}
