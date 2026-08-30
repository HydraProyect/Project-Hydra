using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class SolicitudConexionMicrosoft365Repository(CaeManagerDbContext dbContext) : ISolicitudConexionMicrosoft365Repository
{
    public Task<SolicitudConexionMicrosoft365?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.SolicitudesConexionMicrosoft365.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public void Agregar(SolicitudConexionMicrosoft365 solicitud) => dbContext.SolicitudesConexionMicrosoft365.Add(solicitud);

    public void Eliminar(SolicitudConexionMicrosoft365 solicitud) => dbContext.SolicitudesConexionMicrosoft365.Remove(solicitud);
}
