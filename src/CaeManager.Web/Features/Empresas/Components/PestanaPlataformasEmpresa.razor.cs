namespace CaeManager.Web.Features.Empresas.Components;

public partial class PestanaPlataformasEmpresa
{
    /// <summary>
    /// «Solo incidencias» es el único filtro de la pestaña. Con él marcado, una lista sin grupos es un resultado del filtro
    /// (todo está validado), no una Empresa sin documentación en plataformas: ese caso, «sin datos», es la rama de <c>_filas.Count == 0</c>.
    /// </summary>
    private bool HayFiltrosActivos => _soloIncidencias;

    private static int TotalVisibles(IReadOnlyList<Grupo> grupos) => grupos.Sum(g => g.Visibles.Count);
}
