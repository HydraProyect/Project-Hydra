namespace CaeManager.Domain.Comunicaciones;

public interface IClasificacionRuidoMensajeRepository
{
    void Agregar(ClasificacionRuidoMensaje clasificacion);

    Task<ClasificacionRuidoMensaje?> ObtenerPorMensajeIdAsync(Guid mensajeId, CancellationToken cancellationToken = default);
}
