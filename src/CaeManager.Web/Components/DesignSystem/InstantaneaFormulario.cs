using System.Collections;
using System.Globalization;

namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// P1-E2b: la mitad repetida de «hay cambios sin guardar» en un formulario de drawer o
/// modal. El formulario fija una instantánea de sus campos al terminar de abrirse (con lo
/// que la pantalla haya preseleccionado ya puesto, que no es un cambio de quien edita) y
/// pregunta después si el estado actual difiere de ella. Lo lee
/// <c>AvisoCambiosSinGuardar</c> a través del <c>HayCambios</c> de cada formulario.
///
/// Las colecciones (salvo <see cref="string"/>) se comparan como conjunto: marcar y
/// desmarcar la misma casilla vuelve al estado de partida.
/// </summary>
public sealed class InstantaneaFormulario
{
    private const char Separador = '\u001f';

    private string? _alAbrir;

    /// <summary>Toma la instantánea: se llama al terminar de abrir el formulario.</summary>
    public void Fijar(params object?[] valores) => _alAbrir = Componer(valores);

    /// <summary>Si los valores actuales difieren de la instantánea; sin instantánea, nunca.</summary>
    public bool Difiere(params object?[] valores) => _alAbrir is not null && Componer(valores) != _alAbrir;

    private static string Componer(object?[] valores) =>
        string.Join(Separador, valores.Select(Texto));

    private static string Texto(object? valor) => valor switch
    {
        null => string.Empty,
        string texto => texto,
        IEnumerable coleccion => string.Join(',', coleccion.Cast<object?>().Select(Texto).Order(StringComparer.Ordinal)),
        IFormattable formateable => formateable.ToString(null, CultureInfo.InvariantCulture),
        _ => valor.ToString() ?? string.Empty,
    };
}
