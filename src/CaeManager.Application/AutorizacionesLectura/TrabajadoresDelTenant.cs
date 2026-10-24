using CaeManager.Application.Trabajadores;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.AutorizacionesLectura;

/// <summary>
/// ¿Existen todos estos Trabajadores en el Tenant activo, sin baja? La lectura va por el filtro de Tenant de EF y RLS
/// debajo: un Id de otro Tenant no se encuentra, y responde lo mismo que un Id inventado. La FK compuesta
/// (TenantId, TrabajadorId) lo repite en la base.
/// </summary>
internal static class TrabajadoresDelTenant
{
    public static async Task<bool> ExistenTodosAsync(
        ITrabajadoresQueryContext trabajadoresContext, IReadOnlyCollection<Guid> trabajadorIds, CancellationToken cancellationToken)
    {
        if (trabajadorIds.Count == 0)
            return true;

        var encontrados = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id) && !t.EstaEliminado)
            .CountAsync(cancellationToken);
        return encontrados == trabajadorIds.Count;
    }
}
