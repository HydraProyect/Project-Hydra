using CaeManager.Domain.AutorizacionesLectura;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

/// <summary>
/// Sin <c>Eliminar</c> de la autorización: se revoca, y el rol de runtime no tiene DELETE sobre su tabla. Las filas de
/// Trabajadores sí se borran (asignar es insertar y retirar es borrar); un disparador lo impide si la autorización
/// está revocada.
/// </summary>
public class AutorizacionLecturaRepository(CaeManagerDbContext dbContext) : IAutorizacionLecturaRepository
{
    public Task<AutorizacionLecturaDocumental?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.AutorizacionesLecturaDocumental.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<IReadOnlyList<AutorizacionLecturaTrabajador>> ObtenerTrabajadoresAsync(
        Guid autorizacionId, CancellationToken cancellationToken = default) =>
        await dbContext.AutorizacionesLecturaTrabajadores
            .Where(t => t.AutorizacionId == autorizacionId)
            .ToListAsync(cancellationToken);

    public Task<bool> ExisteNoRevocadaAsync(Guid tenantDestinatarioId, Guid contratoAccesoId, CancellationToken cancellationToken = default) =>
        dbContext.AutorizacionesLecturaDocumental.AnyAsync(
            a => a.TenantDestinatarioId == tenantDestinatarioId && a.ContratoAccesoId == contratoAccesoId && a.RevocadaEnUtc == null,
            cancellationToken);

    public void Agregar(AutorizacionLecturaDocumental autorizacion) => dbContext.AutorizacionesLecturaDocumental.Add(autorizacion);

    public void AgregarTrabajador(AutorizacionLecturaTrabajador trabajador) => dbContext.AutorizacionesLecturaTrabajadores.Add(trabajador);

    public void RetirarTrabajador(AutorizacionLecturaTrabajador trabajador) => dbContext.AutorizacionesLecturaTrabajadores.Remove(trabajador);
}
