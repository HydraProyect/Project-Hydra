namespace CaeManager.Web.Features.Centros.Components;

/// <summary>Un desplegable de Subcontrata en Centro 360: sus filas y su Empresa contratista, si se conoce.</summary>
internal sealed record GrupoSubcontrata<T>(Guid SubcontrataId, string Nombre, string? EmpresaContratistaNombre, IReadOnlyList<T> Filas);

/// <summary>
/// Reparto de las filas de Centro 360 (Trabajadores o Vehículos) como en la maqueta del 21-10, bloque 4: primero las de
/// las Empresas del Centro, sin cabecera, y después un grupo por Subcontrata, por nombre. Conserva el orden de entrada
/// dentro de cada parte. No decide qué filas existen: reparte las que la pantalla ya filtró.
/// </summary>
internal static class GruposPorSubcontrata
{
    public static (IReadOnlyList<T> Principales, IReadOnlyList<GrupoSubcontrata<T>> Subcontratas) Repartir<T>(
        IEnumerable<T> filas, Func<T, Guid?> subcontrataId, Func<T, string> nombreSubcontrata, Func<T, string?> contratista)
    {
        var lista = filas.ToList();
        var principales = lista.Where(f => subcontrataId(f) is null).ToList();
        var grupos = lista.Where(f => subcontrataId(f) is not null)
            .GroupBy(f => subcontrataId(f)!.Value)
            .Select(g => new GrupoSubcontrata<T>(g.Key, nombreSubcontrata(g.First()),
                g.Select(contratista).FirstOrDefault(c => c is not null), g.ToList()))
            .OrderBy(g => g.Nombre, StringComparer.CurrentCulture)
            .ToList();
        return (principales, grupos);
    }
}
