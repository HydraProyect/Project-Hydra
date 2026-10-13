using Microsoft.AspNetCore.WebUtilities;

namespace CaeManager.Web.Exportacion;

/// <summary>Construye el enlace de la vista filtrada para el icono de exportación.</summary>
internal static class EnlaceDeExportacion
{
    internal static string ConCriterios(string ruta, IReadOnlyDictionary<string, string?> criterios) =>
        QueryHelpers.AddQueryString(ruta, criterios.Where(c => !string.IsNullOrWhiteSpace(c.Value)));
}
