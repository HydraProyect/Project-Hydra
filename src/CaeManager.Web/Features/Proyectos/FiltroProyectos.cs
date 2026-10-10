using CaeManager.Application.Common;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Web.Components.DesignSystem;

namespace CaeManager.Web.Features.Proyectos;

/// <summary>
/// Los dos filtros del listado de Proyectos, que se aplican en memoria sobre lo que devuelve
/// <see cref="ObtenerProyectosQuery"/>. Los comparten la página y su exportación: «Exportar esta
/// vista» saca las filas que la pantalla enseña porque las decide este mismo código.
/// </summary>
public static class FiltroProyectos
{
    public const string EstadoAbiertos = "abiertos";
    public const string EstadoCerrados = "cerrados";

    /// <summary>La selección reducida a los dos estados que existen; lo demás se descarta.</summary>
    public static string EstadosValidos(string? seleccion) =>
        SeleccionEstados.Unir(SeleccionEstados.Separar(seleccion).Where(v => v is EstadoAbiertos or EstadoCerrados)) ?? string.Empty;

    public static bool Cumple(ProyectoListaDto proyecto, string? estados, string? busqueda)
    {
        // Varios estados marcados: pasa el Proyecto que esté en cualquiera. Sin ninguno, todos.
        var marcados = SeleccionEstados.Separar(estados);
        var cumpleEstado = marcados.Count == 0
            || marcados.Contains(proyecto.EstaAbierto ? EstadoAbiertos : EstadoCerrados);

        return cumpleEstado && CumpleBusqueda(proyecto, busqueda);
    }

    public static bool CumpleBusqueda(ProyectoListaDto proyecto, string? busqueda)
    {
        var termino = (busqueda ?? string.Empty).Trim();
        return termino.Length == 0
            || TextoDeBusqueda.Contiene(proyecto.Nombre, termino)
            || TextoDeBusqueda.Contiene(proyecto.CentroNombre, termino);
    }
}
