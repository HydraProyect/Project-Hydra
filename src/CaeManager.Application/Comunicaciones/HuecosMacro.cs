using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace CaeManager.Application.Comunicaciones;

/// <summary>Los únicos huecos que una macro puede llevar: cada uno se rellena con una opción de catálogo que elige el Gestor CAE.</summary>
public enum TipoHuecoMacro
{
    Centro,
    Trabajador,
    Fecha,
}

/// <summary>
/// Valores elegidos por el Gestor CAE para los huecos de una macro. <c>Centro</c> y <c>Trabajador</c>
/// son el texto de una opción de un catálogo que el alcance actual ya permite leer; <c>Fecha</c>, un
/// día elegido en el selector. Un valor <c>null</c> es «sin elegir»: nunca se sustituye por uno inferido.
/// </summary>
public sealed record ValoresHuecosMacro(string? Centro, string? Trabajador, DateOnly? Fecha);

/// <summary>Resultado de rellenar una macro. <c>Texto</c> es <c>null</c> mientras quede algún hueco sin elegir.</summary>
public sealed record MacroRellenada(string? Texto, IReadOnlyList<TipoHuecoMacro> Pendientes);

/// <summary>
/// Sintaxis CERRADA de huecos tipados en el cuerpo de una macro: exactamente <c>{{centro}}</c>,
/// <c>{{trabajador}}</c> y <c>{{fecha}}</c>, en minúsculas y sin espacios. Cualquier otra cosa entre
/// llaves dobles se queda como texto: no hay expresiones, ni nombres de campo libres, ni
/// <c>EtiquetaProposito</c> (texto libre editable, superficie de inyección).
///
/// <para>
/// Todo valor se inserta ESCAPADO (<see cref="WebUtility.HtmlEncode(string?)"/>): el cuerpo de la
/// respuesta viaja como HTML en el correo, y el nombre de un Centro o de un Trabajador es texto
/// escrito por una persona. La sustitución se hace en una sola pasada sobre el cuerpo original, así
/// que un valor que contenga <c>{{fecha}}</c> no se vuelve a expandir. Una macro sin huecos no pasa
/// por aquí y se comporta como siempre (copiar el cuerpo tal cual).
/// </para>
/// </summary>
public static partial class HuecosMacro
{
    [GeneratedRegex(@"\{\{(centro|trabajador|fecha)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PatronHueco();

    public const string FormatoFecha = "dd/MM/yyyy";

    /// <summary>Huecos distintos que lleva el cuerpo, en orden de aparición.</summary>
    public static IReadOnlyList<TipoHuecoMacro> Detectar(string? cuerpo)
    {
        if (string.IsNullOrEmpty(cuerpo)) return [];
        return PatronHueco().Matches(cuerpo).Select(m => Tipo(m.Groups[1].Value)).Distinct().ToList();
    }

    public static bool TieneHuecos(string? cuerpo) => Detectar(cuerpo).Count > 0;

    /// <summary>
    /// Rellena el cuerpo. Si falta algún valor, <c>Texto</c> es <c>null</c>: la macro no se inserta a medias.
    /// </summary>
    public static MacroRellenada Rellenar(string cuerpo, ValoresHuecosMacro valores)
    {
        var pendientes = Detectar(cuerpo).Where(t => Valor(t, valores) is null).ToList();
        if (pendientes.Count > 0) return new MacroRellenada(null, pendientes);

        var texto = PatronHueco().Replace(cuerpo, m => WebUtility.HtmlEncode(Valor(Tipo(m.Groups[1].Value), valores)!));
        return new MacroRellenada(texto, []);
    }

    /// <summary>
    /// Vista previa para mostrar mientras se eligen los valores: lo elegido, tal cual, y un marcador
    /// «‹centro›» donde aún falta. Es texto para pintar (Blazor lo escapa): nunca se inserta en la respuesta.
    /// </summary>
    public static string Previsualizar(string cuerpo, ValoresHuecosMacro valores) =>
        PatronHueco().Replace(cuerpo, m =>
        {
            var tipo = Tipo(m.Groups[1].Value);
            return Valor(tipo, valores) ?? $"‹{m.Groups[1].Value}›";
        });

    private static TipoHuecoMacro Tipo(string nombre) => nombre switch
    {
        "centro" => TipoHuecoMacro.Centro,
        "trabajador" => TipoHuecoMacro.Trabajador,
        _ => TipoHuecoMacro.Fecha,
    };

    private static string? Valor(TipoHuecoMacro tipo, ValoresHuecosMacro v) => tipo switch
    {
        TipoHuecoMacro.Centro => string.IsNullOrWhiteSpace(v.Centro) ? null : v.Centro,
        TipoHuecoMacro.Trabajador => string.IsNullOrWhiteSpace(v.Trabajador) ? null : v.Trabajador,
        _ => v.Fecha?.ToString(FormatoFecha, CultureInfo.InvariantCulture),
    };
}
