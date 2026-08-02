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
        navigation.NavigateTo(
            navigation.GetUriWithQueryParameters(
                new Dictionary<string, object?> { [nombreParametro] = string.IsNullOrWhiteSpace(valor) ? null : valor }),
            replace: true);
}
