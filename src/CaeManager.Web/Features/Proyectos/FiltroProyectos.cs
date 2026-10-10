using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Web.Components.DesignSystem;

namespace CaeManager.Web.Features.Proyectos;

/// <summary>
/// El filtro de estado del listado de Proyectos tal como viaja en la URL (estados separados por coma) y
/// su traducción al parámetro de <see cref="ObtenerProyectosQuery"/>. Lo comparten la página y su
/// exportación: «Exportar esta vista» saca las filas que la pantalla enseña porque las dos piden lo mismo
/// a la misma consulta.
/// </summary>
public static class FiltroProyectos
{
    public const string EstadoAbiertos = ObtenerProyectosQuery.EstadoAbiertos;
    public const string EstadoCerrados = ObtenerProyectosQuery.EstadoCerrados;

    /// <summary>La selección reducida a los dos estados que existen; lo demás se descarta.</summary>
    public static string EstadosValidos(string? seleccion) =>
        SeleccionEstados.Unir(SeleccionEstados.Separar(seleccion).Where(v => v is EstadoAbiertos or EstadoCerrados)) ?? string.Empty;

    /// <summary>
    /// La selección como filtro de la consulta. Con los dos estados marcados pasa cualquier Proyecto, igual
    /// que sin ninguno: <c>null</c>.
    /// </summary>
    public static bool? SoloAbiertos(string? seleccion)
    {
        var marcados = SeleccionEstados.Separar(seleccion);
        var abiertos = marcados.Contains(EstadoAbiertos);
        var cerrados = marcados.Contains(EstadoCerrados);
        return abiertos == cerrados ? null : abiertos;
    }
}
