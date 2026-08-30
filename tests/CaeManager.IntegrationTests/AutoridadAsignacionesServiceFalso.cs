using CaeManager.Application.Common;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Doble de <see cref="IAutoridadAsignacionesService"/> para tests que
/// construyen los handlers a mano.
///
/// <para>
/// <b>Conserva la comprobación de existencia contra la base real</b>, no solo
/// la de ámbito: el servicio verdadero hace las dos, y un doble que dijera
/// «sí» a cualquier Guid dejaría pasar en verde los tests que verifican el
/// rechazo de un Id de otro tenant (P0-1) — el doble estaría probando su
/// propia permisividad, no el comando.
/// </para>
///
/// <para>
/// Por defecto concede autoridad sobre <b>todo centro que exista</b>, que es
/// el rol Administrador y el supuesto de los tests que ya existían. Pasando
/// <paramref name="centrosConAutoridad"/> se acota, para los tests que sí
/// ejercitan la restricción de ámbito.
/// </para>
/// </summary>
public class AutoridadAsignacionesServiceFalso(
    CaeManagerDbContext dbContext,
    IReadOnlyList<Guid>? centrosConAutoridad = null,
    IReadOnlyList<Guid>? trabajadoresConAutoridad = null) : IAutoridadAsignacionesService
{
    public async Task<bool> PuedeModificarAsignacionesDelCentroAsync(
        Guid centroId, CancellationToken cancellationToken = default) =>
        (await FiltrarCentrosConAutoridadAsync([centroId], cancellationToken)).Count == 1;

    public async Task<IReadOnlyList<Guid>> FiltrarCentrosConAutoridadAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken = default)
    {
        if (centroIds.Count == 0) return [];

        var existentes = await dbContext.Centros
            .Where(c => centroIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (centrosConAutoridad is null) return existentes;

        var permitidos = centrosConAutoridad.ToHashSet();
        return existentes.Where(permitidos.Contains).ToList();
    }

    public async Task<bool> PuedeModificarAsignacionesDelTrabajadorAsync(
        Guid trabajadorId, CancellationToken cancellationToken = default) =>
        (await FiltrarTrabajadoresConAutoridadAsync([trabajadorId], cancellationToken)).Count == 1;

    public async Task<IReadOnlyList<Guid>> FiltrarTrabajadoresConAutoridadAsync(
        IReadOnlyList<Guid> trabajadorIds, CancellationToken cancellationToken = default)
    {
        if (trabajadorIds.Count == 0) return [];

        var existentes = await dbContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        if (trabajadoresConAutoridad is null) return existentes;

        var permitidos = trabajadoresConAutoridad.ToHashSet();
        return existentes.Where(permitidos.Contains).ToList();
    }
}
