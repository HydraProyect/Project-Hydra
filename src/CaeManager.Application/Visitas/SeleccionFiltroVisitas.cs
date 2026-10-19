using System.Text.Json;

namespace CaeManager.Application.Visitas;

/// <summary>Selección múltiple en la URL. JSON conserva nombres con comas, puntos y coma y comillas.</summary>
public static class SeleccionFiltroVisitas
{
    public static IReadOnlyList<string> Leer(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return [];
        if (!valor.StartsWith('[')) return [valor];
        try
        {
            return (JsonSerializer.Deserialize<string[]>(valor) ?? [])
                .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToList();
        }
        catch (JsonException) { return []; }
    }

    public static string Escribir(IEnumerable<string> valores)
    {
        var seleccion = valores.Distinct(StringComparer.Ordinal).ToList();
        return seleccion.Count switch
        {
            0 => string.Empty,
            1 when !seleccion[0].StartsWith('[') => seleccion[0],
            _ => JsonSerializer.Serialize(seleccion)
        };
    }

    public static int? Ventana(string? valor)
    {
        var dias = Leer(valor).Where(v => v is "0" or "7" or "14").Select(int.Parse).ToList();
        return dias.Count == 0 ? null : dias.Max();
    }
}
