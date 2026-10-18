using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.CuadroDireccion.Components;

/// <summary>Una Empresa en la dispersión: abscisa, ordenada y tamaño (la facturación).</summary>
public sealed record PuntoDispersion(string Nombre, double X, double Y, double Tamano, bool Destacado);

/// <summary>Una burbuja ya colocada: posición en % del plano (izquierda y abajo), radio en píxeles.</summary>
public sealed record BurbujaGeometria(string Nombre, double X, double Y, double Radio, bool Destacado, string Titulo);

/// <summary>Una marca de un eje: su fracción del plano (0 a 1) y el valor ya formateado.</summary>
public sealed record MarcaDispersion(double Fraccion, string Texto);

public partial class GraficoDispersion : ComponentBase
{
    [Parameter] public string Id { get; set; } = string.Empty;
    [Parameter] public string EtiquetaAria { get; set; } = string.Empty;
    [Parameter] public string EtiquetaX { get; set; } = string.Empty;
    [Parameter] public string EtiquetaY { get; set; } = string.Empty;
    [Parameter] public IReadOnlyList<PuntoDispersion> Puntos { get; set; } = [];

    /// <summary>Texto que describe cada punto en su tooltip.</summary>
    [Parameter] public Func<PuntoDispersion, string> Describir { get; set; } = p => p.Nombre;

    /// <summary>Cómo se rotulan los valores de los ejes y la mediana.</summary>
    [Parameter] public Func<double, string> FormatoX { get; set; } = v => v.ToString("N0", CuadroDireccionUi.Castellano);
    [Parameter] public Func<double, string> FormatoY { get; set; } = v => v.ToString("N0", CuadroDireccionUi.Castellano);
    [Parameter] public Func<double, string> FormatoMediana { get; set; } = v => v.ToString("N0", CuadroDireccionUi.Castellano);

    /// <summary>Leyenda de colores (el nombre de cada color y el color del diseño); los valores no se usan.</summary>
    [Parameter] public IReadOnlyList<SerieGrafico> Leyenda { get; set; } = [];

    private const double RadioMinimo = 4;
    private const double RadioMaximo = 18;

    private double? Mediana
    {
        get
        {
            if (Puntos.Count == 0) return null;
            var ordenadas = Puntos.Select(p => p.Y).OrderBy(y => y).ToList();
            var medio = ordenadas.Count / 2;
            return ordenadas.Count % 2 == 1 ? ordenadas[medio] : (ordenadas[medio - 1] + ordenadas[medio]) / 2;
        }
    }

    private double MaximoX => Puntos.Count == 0 ? 1 : Redondeado(Math.Max(Puntos.Max(p => p.X) * 1.12, 1)).Maximo;
    private double MaximoY => Puntos.Count == 0 ? 1 : Redondeado(Math.Max(Puntos.Max(p => p.Y) * 1.15, 1)).Maximo;

    /// <summary>El máximo de un eje, redondeado a 1, 2 o 5 por una potencia de diez, y en cuántas divisiones se parte.</summary>
    private static (double Maximo, int Divisiones) Redondeado(double maximo)
    {
        var potencia = Math.Pow(10, Math.Floor(Math.Log10(maximo)));
        var fraccion = maximo / potencia;
        var paso = fraccion <= 1 ? 1 : fraccion <= 2 ? 2 : fraccion <= 5 ? 5 : 10;
        return (paso * potencia, paso == 2 ? 4 : 5);
    }

    private IReadOnlyList<MarcaDispersion> MarcasY => Marcas(MaximoY, FormatoY);

    private IReadOnlyList<MarcaDispersion> MarcasX => Marcas(MaximoX, FormatoX);

    private static IReadOnlyList<MarcaDispersion> Marcas(double maximo, Func<double, string> formato)
    {
        var (eje, divisiones) = Redondeado(maximo);
        return Enumerable.Range(0, divisiones + 1).Select(d => new MarcaDispersion((double)d / divisiones, formato(eje * d / divisiones))).ToList();
    }

    private static string Texto(double valor) => valor.ToString("0.##", CultureInfo.InvariantCulture);

    private IReadOnlyList<BurbujaGeometria> Geometria
    {
        get
        {
            if (Puntos.Count == 0) return [];
            var maximoTamano = Math.Max(Puntos.Max(p => p.Tamano), 1);
            return Puntos
                .OrderByDescending(p => p.Tamano)
                .Select(p => new BurbujaGeometria(
                    p.Nombre,
                    p.X / MaximoX * 100,
                    p.Y / MaximoY * 100,
                    RadioMinimo + Math.Sqrt(Math.Max(p.Tamano, 0) / maximoTamano) * (RadioMaximo - RadioMinimo),
                    p.Destacado,
                    Describir(p)))
                .ToList();
        }
    }
}
