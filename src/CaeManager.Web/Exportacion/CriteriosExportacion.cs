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

    /// <summary>
    /// El valor, si tiene forma de nombre de estado o de columna (solo letras, hasta 40); si no,
    /// nada. Un parámetro de consulta es texto libre: lo que no sea un nombre no llega al rastro.
    /// </summary>
    public static string? SoloNombre(string? valor) =>
        valor is { Length: > 0 and <= 40 } && valor.All(char.IsAsciiLetter) ? valor : null;
}
