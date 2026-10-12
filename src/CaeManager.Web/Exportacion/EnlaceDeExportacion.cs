using Microsoft.AspNetCore.WebUtilities;

namespace CaeManager.Web.Exportacion;

/// <summary>El mismo enlace de vista filtrada para el icono y las entradas de exportación del menú.</summary>
internal static class EnlaceDeExportacion
{
    internal static string ConCriterios(string ruta, IReadOnlyDictionary<string, string?> criterios) =>
        QueryHelpers.AddQueryString(ruta, criterios.Where(c => !string.IsNullOrWhiteSpace(c.Value)));
}
