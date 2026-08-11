using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Tests.Comunicaciones;

public class ClasificacionRuidoMensajeServiceFalso : IClasificacionRuidoMensajeService
{
    public List<(Guid SugerenciaId, Guid ClienteId, bool EsNotificacionAutomatica)> Llamadas { get; } = [];

    public List<(Guid ClienteId, Guid ProveedorPlataformaCaeId, ResumenAgregadoGestionDto Resumen)> LlamadasResumen { get; } = [];

    public bool SinCambiosAResponder { get; set; }

    public Task ProcesarAsync(
        SugerenciaGestionCorreo sugerencia, Guid clienteId, bool esNotificacionAutomatica, CancellationToken cancellationToken = default)
    {
        Llamadas.Add((sugerencia.Id, clienteId, esNotificacionAutomatica));
        return Task.CompletedTask;
    }

    public Task<bool> ProcesarResumenAsync(
        Guid clienteId, Guid proveedorPlataformaCaeId, ResumenAgregadoGestionDto resumen, CancellationToken cancellationToken = default)
    {
        LlamadasResumen.Add((clienteId, proveedorPlataformaCaeId, resumen));
        return Task.FromResult(SinCambiosAResponder);
    }
}
