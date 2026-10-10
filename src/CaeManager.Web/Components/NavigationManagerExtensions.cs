using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Components;

/// <summary>
/// Project-Hydra-Negocio/tecnico/docs/archive/design/UX_PATTERNS.md § "Estado de filtros persiste en la URL (query string)
/// para que se pueda compartir/recargar sin perder el contexto" — P1-18 de
/// Project-Hydra-Negocio/MATURITY_REVIEW.md. Cada página con filtros llama a esto
/// cuando el usuario cambia uno, en vez de guardarlo solo en un campo
/// privado del componente.
/// </summary>
public static class NavigationManagerExtensions
{
    /// <summary>
    /// Marca de la navegación con la que una página escribe sus propios filtros en su URL. Viaja como estado de la
    /// entrada del historial solo para que <c>AvisoCambiosSinGuardar</c> la reconozca: la página no sale de ningún
    /// sitio y sus formularios siguen montados, así que no hay nada que preguntar. Quien escriba en la URL algo que
    /// SÍ cierra un formulario (otro Cliente en Proyectos) pregunta antes por su cuenta, como ya hacía.
    /// </summary>
    public const string EstadoEscrituraDeFiltros = "talveg:escritura-de-filtros";

    /// <summary>
    /// Actualiza un parámetro de la URL actual sin recargar la página
    /// (<c>replace: true</c> para no llenar el historial de un clic por
    /// tecla). Un valor vacío o en blanco quita el parámetro en vez de
    /// dejarlo como cadena vacía en la URL.
    /// </summary>
    public static void ActualizarFiltroEnUrl(this NavigationManager navigation, string nombreParametro, string? valor) =>
        navigation.ActualizarFiltrosEnUrl(new Dictionary<string, string?> { [nombreParametro] = valor });

    /// <summary>
    /// Igual que <see cref="ActualizarFiltroEnUrl"/> pero para varios
    /// parámetros a la vez, en una única navegación — llamarlo varias veces
    /// seguidas (uno por filtro) arriesga que cada `NavigateTo` lea la URL
    /// todavía sin el cambio del anterior y se pisen entre sí.
    /// </summary>
    public static void ActualizarFiltrosEnUrl(this NavigationManager navigation, IReadOnlyDictionary<string, string?> parametros)
    {
        var normalizados = new Dictionary<string, object?>();
        foreach (var (nombreParametro, valor) in parametros)
            normalizados[nombreParametro] = string.IsNullOrWhiteSpace(valor) ? null : valor;

        navigation.NavigateTo(
            navigation.GetUriWithQueryParameters(normalizados),
            new NavigationOptions { ReplaceHistoryEntry = true, HistoryEntryState = EstadoEscrituraDeFiltros });
    }
}
