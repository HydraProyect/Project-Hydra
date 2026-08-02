using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Components;

/// <summary>
/// UX_PATTERNS.md § "Estado de filtros persiste en la URL (query string)
/// para que se pueda compartir/recargar sin perder el contexto" — P1-18 de
/// docs/business/MATURITY_REVIEW.md. Cada página con filtros llama a esto
/// cuando el usuario cambia uno, en vez de guardarlo solo en un campo
/// privado del componente.
/// </summary>
public static class NavigationManagerExtensions
{
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

        navigation.NavigateTo(navigation.GetUriWithQueryParameters(normalizados), replace: true);
    }
}
