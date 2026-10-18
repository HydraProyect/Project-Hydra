using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Tests.Comunicaciones;

public class AlertaCriticaServiceFalso : IAlertaCriticaService
{
    public List<Mensaje> Mensajes { get; } = [];
    public Task ProcesarAsync(Mensaje mensaje, CancellationToken cancellationToken)
    {
        Mensajes.Add(mensaje);
        return Task.CompletedTask;
    }
}
