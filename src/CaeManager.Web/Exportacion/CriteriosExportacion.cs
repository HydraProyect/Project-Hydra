namespace CaeManager.Web.Exportacion;

/// <summary>
/// Los criterios de «Exportar esta vista» que acompañan al rastro de una
/// exportación (<c>IRegistroExportacionService</c>): solo los que viajaron con
/// valor. Vacío significa «Exportar todo».
/// </summary>
public static class CriteriosExportacion
{
    public static IReadOnlyDictionary<string, string> Desde(params (string Nombre, string? Valor)[] criterios) =>
        criterios
            .Where(c => !string.IsNullOrWhiteSpace(c.Valor))
            .ToDictionary(c => c.Nombre, c => c.Valor!);
}
