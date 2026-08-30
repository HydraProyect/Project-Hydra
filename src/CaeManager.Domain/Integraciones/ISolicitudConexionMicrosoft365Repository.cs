namespace CaeManager.Domain.Integraciones;

public interface ISolicitudConexionMicrosoft365Repository
{
    Task<SolicitudConexionMicrosoft365?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(SolicitudConexionMicrosoft365 solicitud);

    /// <summary>Consumo de un solo uso: se borra en cuanto se valida, para que ni un replay del mismo callback ni una fuga del "state" puedan reutilizarla.</summary>
    void Eliminar(SolicitudConexionMicrosoft365 solicitud);
}
