using CaeManager.Domain.Centros;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros;

/// <summary>
/// Único punto de lectura de «qué Centros no requieren gestión CAE» (P1-X2).
/// Cumplimiento, alertas de documentación faltante, paquete de acreditación de
/// la Visita y Mi trabajo lo consultan aquí para no exigir nada a esos
/// Centros; tenerlo en un solo sitio evita que un camino nuevo se olvide del
/// filtro y vuelva a pedir documentación a un Centro que no la quiere.
/// </summary>
public static class CentrosSinGestionCae
{
    /// <summary>Subconjunto de <paramref name="centroIds"/> que no requiere gestión CAE.</summary>
    public static async Task<IReadOnlySet<Guid>> FiltrarAsync(
        ICentrosQueryContext centrosContext, IEnumerable<Guid> centroIds, CancellationToken cancellationToken)
    {
        var ids = centroIds.Distinct().ToList();
        if (ids.Count == 0)
            return new HashSet<Guid>();

        return (await centrosContext.Centros
            .Where(c => ids.Contains(c.Id) && c.GestionCae == ModalidadGestionCae.SinGestionCae)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken))
            .ToHashSet();
    }
}
