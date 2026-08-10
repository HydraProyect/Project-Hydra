using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Tests.Comunicaciones;

public class ClasificacionRuidoMensajeServiceFalso : IClasificacionRuidoMensajeService
{
    public List<(Guid SugerenciaId, Guid ClienteId, bool EsNotificacionAutomatica)> Llamadas { get; } = [];

    public Task ProcesarAsync(
        SugerenciaGestionCorreo sugerencia, Guid clienteId, bool esNotificacionAutomatica, CancellationToken cancellationToken = default)
    {
        Llamadas.Add((sugerencia.Id, clienteId, esNotificacionAutomatica));
        return Task.CompletedTask;
    }
}
