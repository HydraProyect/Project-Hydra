namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// La selección de <see cref="FranjaEstado"/> tal como viaja en la URL: los estados de código separados por
/// coma (<c>?estado=Vencido,Urgente,Proximo</c>). Un enlace antiguo con un solo estado (<c>?estado=Urgente</c>)
/// sigue siendo una selección válida, de un elemento.
/// </summary>
public static class SeleccionEstados
{
    public const char Separador = ',';

    /// <summary>Los valores de la selección, sin vacíos ni repetidos, en el orden en que llegan.</summary>
    public static IReadOnlyList<string> Separar(string? valor) =>
        string.IsNullOrWhiteSpace(valor)
            ? []
            : valor.Split(Separador, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();

    /// <summary>
    /// La selección como estados de un enum. Lo que no es un nombre del enum se descarta (un número tampoco
    /// vale: la URL lleva nombres): un valor desconocido no filtra.
    /// </summary>
    public static IReadOnlyList<TEnum> Separar<TEnum>(string? valor) where TEnum : struct, Enum
    {
        var estados = new List<TEnum>();
        foreach (var texto in Separar(valor))
        {
            if (!int.TryParse(texto, out _) && Enum.TryParse<TEnum>(texto, out var estado) && Enum.IsDefined(estado))
                estados.Add(estado);
        }

        return estados;
    }

    /// <summary><c>null</c> si no queda ninguno: así el parámetro desaparece de la URL.</summary>
    public static string? Unir(IEnumerable<string> valores)
    {
        var unidos = string.Join(Separador, valores.Distinct(StringComparer.Ordinal));
        return unidos.Length == 0 ? null : unidos;
    }
}
