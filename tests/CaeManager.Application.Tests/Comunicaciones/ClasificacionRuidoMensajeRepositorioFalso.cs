using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Tests.Comunicaciones;

public class ClasificacionRuidoMensajeRepositorioFalso : IClasificacionRuidoMensajeRepository
{
    public List<ClasificacionRuidoMensaje> Clasificaciones { get; } = [];

    public void Agregar(ClasificacionRuidoMensaje clasificacion) => Clasificaciones.Add(clasificacion);

    public Task<ClasificacionRuidoMensaje?> ObtenerPorMensajeIdAsync(Guid mensajeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Clasificaciones.FirstOrDefault(c => c.MensajeId == mensajeId));
}
