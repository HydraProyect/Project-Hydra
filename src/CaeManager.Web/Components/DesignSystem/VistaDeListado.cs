using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// La vista de un listado como diccionario <c>{ "&lt;parámetro&gt;": "&lt;valor&gt;" }</c> de los parámetros
/// de VISTA de su URL. Es lo que comparten los filtros guardados (<see cref="FiltrosGuardadosDeListado"/>)
/// y la vista recordada (<see cref="VistaRecordadaDeListado"/>): las dos guardan lo que viaja en la URL
/// y las dos lo devuelven a la página por el mismo camino, así que se lee y se escribe en un solo sitio.
///
/// <para>
/// La página declara una lista blanca. Lo que no esté en ella (<c>accion</c>, ids de fila abierta,
/// precargas de un alta) ni se guarda ni se aplica, venga de la URL o de un JSON guardado.
/// </para>
/// </summary>
public static class VistaDeListado
{
    /// <summary>
    /// Los parámetros de la lista blanca que lleva esa dirección, con su valor y en el orden de la
    /// lista. El nombre se compara sin distinguir mayúsculas, como hace el enlace de parámetros de
    /// consulta, y se guarda con la grafía de la lista. Un parámetro vacío no está.
    /// </summary>
    public static Dictionary<string, string> DeLaUrl(Uri direccion, IReadOnlyList<string> parametrosDeVista)
    {
        var consulta = QueryHelpers.ParseQuery(direccion.Query);
        var vista = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var parametro in parametrosDeVista)
        {
            if (consulta.TryGetValue(parametro, out var valores)
                && valores.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) is { } valor)
                vista[parametro] = valor;
        }

        return vista;
    }

    /// <summary>
    /// El diccionario guardado. Solo cuentan las propiedades de texto con contenido; un JSON ilegible
    /// o que no es un objeto (una fila que no escribió esta pieza) no es una vista: <c>null</c>.
    /// </summary>
    public static Dictionary<string, string>? Leer(string? valoresJson)
    {
        if (string.IsNullOrWhiteSpace(valoresJson))
            return null;

        try
        {
            using var documento = JsonDocument.Parse(valoresJson);
            if (documento.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var vista = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var propiedad in documento.RootElement.EnumerateObject())
            {
                if (propiedad.Value.ValueKind == JsonValueKind.String
                    && propiedad.Value.GetString() is { } valor && !string.IsNullOrWhiteSpace(valor))
                    vista[propiedad.Name] = valor;
            }

            return vista;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// La vista entera que recibe la página: un valor por CADA parámetro de la lista blanca,
    /// <c>null</c> el que la vista no trae (la página lo quita). Lo que la vista traiga fuera de la
    /// lista no llega a la página.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Completa(
        IReadOnlyList<string> parametrosDeVista, IReadOnlyDictionary<string, string> vista) =>
        parametrosDeVista.ToDictionary(p => p, p => vista.GetValueOrDefault(p), StringComparer.Ordinal);

    /// <summary>
    /// La vista acotada a la lista blanca y en su orden: dos vistas iguales dan el mismo texto, sea
    /// cual sea el orden en que llegaron sus parámetros. Es lo que se guarda como <c>ValoresJson</c>.
    /// </summary>
    public static string Serializar(IReadOnlyList<string> parametrosDeVista, IReadOnlyDictionary<string, string> vista)
    {
        var ordenada = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parametro in parametrosDeVista)
        {
            if (vista.TryGetValue(parametro, out var valor) && !string.IsNullOrWhiteSpace(valor))
                ordenada[parametro] = valor;
        }

        return JsonSerializer.Serialize(ordenada);
    }
}
