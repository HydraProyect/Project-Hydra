using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.CuadroDireccion.Components;

/// <summary>Una serie del gráfico: su nombre, el color (variable CSS del diseño) y un valor por etiqueta; null = sin dato.</summary>
public sealed record SerieGrafico(string Nombre, string Color, IReadOnlyList<double?> Valores);

public sealed record BarraGeometria(double X, double Y, double Ancho, double Alto, string Color, bool Resaltada, string? Valor);

public sealed record PuntoGeometria(double X, double Y);

public sealed record LineaGeometria(string Color, string Texto, IReadOnlyList<PuntoGeometria> Puntos);

public partial class GraficoSerie : ComponentBase
{
    private const double MargenInferior = 22;

    [Parameter] public string Id { get; set; } = string.Empty;
    [Parameter] public string EtiquetaAria { get; set; } = string.Empty;
    [Parameter] public IReadOnlyList<string> Etiquetas { get; set; } = [];

    /// <summary>Series apiladas en barras, de abajo arriba.</summary>
    [Parameter] public IReadOnlyList<SerieGrafico> Barras { get; set; } = [];

    /// <summary>Línea opcional con su propio eje.</summary>
    [Parameter] public SerieGrafico? Linea { get; set; }

    /// <summary>Varias líneas que comparten el mismo eje (una por año en la Comparativa anual).</summary>
    [Parameter] public IReadOnlyList<SerieGrafico> Comparadas { get; set; } = [];

    [Parameter] public bool MostrarValores { get; set; }
    [Parameter] public bool Compacto { get; set; }

    /// <summary>Posiciones de las barras que se resaltan (los meses pico).</summary>
    [Parameter] public IReadOnlySet<int>? Resaltar { get; set; }

    [Parameter] public string ColorResaltado { get; set; } = "var(--color-warning-500)";
    [Parameter] public Func<double, string> FormatoBarra { get; set; } = v => v.ToString("N0", CuadroDireccionUi.Castellano);
    [Parameter] public Func<double, string> FormatoLinea { get; set; } = v => v.ToString("N0", CuadroDireccionUi.Castellano);

    private double Ancho => Compacto ? 120 : 640;
    private double Alto => Compacto ? 32 : 220;
    private double AltoUtil => Compacto ? Alto : Alto - MargenInferior;
    private double Margen => Compacto ? 2 : 8;
    private double Hueco => Etiquetas.Count == 0 ? 0 : (Ancho - 2 * Margen) / Etiquetas.Count;
    private string ColorLinea => Linea?.Color ?? "var(--color-warning-500)";
    private double PorcentajeAncho => Etiquetas.Count == 0 ? 0 : Math.Round(100.0 / Etiquetas.Count, 3);

    private IReadOnlyList<SerieGrafico> Leyenda => [.. Barras, .. Comparadas, .. (Linea is null ? [] : new[] { Linea })];

    /// <summary>Un &lt;text&gt; de SVG: Razor reserva esa etiqueta y no admite atributos en ella, así que se emite como marcado ya codificado.</summary>
    private static MarkupString TextoSvg(string clase, double x, double y, string contenido, string ancla = "middle") =>
        new($"<text class=\"{clase}\" x=\"{Texto(x)}\" y=\"{Texto(y)}\" text-anchor=\"{ancla}\">{System.Net.WebUtility.HtmlEncode(contenido)}</text>");

    private static string Texto(double valor) => valor.ToString("0.##", CultureInfo.InvariantCulture);

    private IEnumerable<int> Posiciones => Enumerable.Range(0, Etiquetas.Count);

    private string PorcentajeIzquierda(int i) => Texto(i * PorcentajeAncho);

    private double CentroDe(int i) => Margen + Hueco * (i + 0.5);

    private double TotalApilado(int i) => Barras.Sum(s => s.Valores.ElementAtOrDefault(i) ?? 0);

    private double MaximoBarras => Etiquetas.Count == 0 ? 0 : Enumerable.Range(0, Etiquetas.Count).Max(TotalApilado);

    private IReadOnlyList<BarraGeometria> GeometriaBarras
    {
        get
        {
            var maximo = MaximoBarras;
            if (Compacto || maximo <= 0) return [];

            var ancho = Hueco * 0.6;
            var resultado = new List<BarraGeometria>();
            for (var i = 0; i < Etiquetas.Count; i++)
            {
                var acumulado = 0.0;
                var x = Margen + Hueco * i + (Hueco - ancho) / 2;
                foreach (var serie in Barras)
                {
                    var valor = serie.Valores.ElementAtOrDefault(i) ?? 0;
                    if (valor <= 0) continue;
                    var alto = valor / maximo * (AltoUtil - 14);
                    acumulado += alto;
                    var resaltada = Resaltar?.Contains(i) == true;
                    resultado.Add(new BarraGeometria(
                        x, AltoUtil - acumulado, ancho, alto,
                        resaltada ? ColorResaltado : serie.Color, resaltada,
                        Barras.Count == 1 ? FormatoBarra(valor) : null));
                }
            }

            return resultado;
        }
    }

    private IReadOnlyList<PuntoGeometria> GeometriaPuntos
    {
        get
        {
            if (Linea is null) return [];

            var valores = Linea.Valores;
            var conDato = valores.Where(v => v is not null).Select(v => v!.Value).ToList();
            if (conDato.Count == 0) return [];

            var maximo = conDato.Max();
            var minimo = Compacto ? conDato.Min() : 0;
            var rango = maximo - minimo;
            var altoUtil = Compacto ? Alto - 6 : AltoUtil - 14;
            var basePosicion = Compacto ? Alto - 3 : AltoUtil;
            var puntos = new List<PuntoGeometria>();
            for (var i = 0; i < valores.Count; i++)
            {
                if (valores[i] is not { } valor) continue;
                var fraccion = rango <= 0 ? 0.5 : (valor - minimo) / rango;
                puntos.Add(new PuntoGeometria(CentroDe(i), basePosicion - fraccion * altoUtil));
            }

            return puntos;
        }
    }

    private string PuntosLinea => string.Join(' ', GeometriaPuntos.Select(p => $"{Texto(p.X)},{Texto(p.Y)}"));

    private IReadOnlyList<LineaGeometria> GeometriaComparadas
    {
        get
        {
            if (Comparadas.Count == 0) return [];

            var maximo = Comparadas.SelectMany(s => s.Valores).Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(0).Max();
            var resultado = new List<LineaGeometria>();
            foreach (var serie in Comparadas)
            {
                var puntos = new List<PuntoGeometria>();
                for (var i = 0; i < serie.Valores.Count; i++)
                {
                    if (serie.Valores[i] is not { } valor) continue;
                    puntos.Add(new PuntoGeometria(CentroDe(i), AltoUtil - (maximo <= 0 ? 0 : valor / maximo) * (AltoUtil - 14)));
                }

                resultado.Add(new LineaGeometria(serie.Color, string.Join(' ', puntos.Select(p => $"{Texto(p.X)},{Texto(p.Y)}")), puntos));
            }

            return resultado;
        }
    }

    private string TextoTip(int i)
    {
        var partes = Barras.Select(s => $"{s.Nombre}: {(s.Valores.ElementAtOrDefault(i) is { } v ? FormatoBarra(v) : "—")}").ToList();
        partes.AddRange(Comparadas.Select(s => $"{s.Nombre}: {(s.Valores.ElementAtOrDefault(i) is { } c ? FormatoBarra(c) : "—")}"));
        if (Linea is not null)
            partes.Add($"{Linea.Nombre}: {(Linea.Valores.ElementAtOrDefault(i) is { } l ? FormatoLinea(l) : "—")}");
        return $"{Etiquetas[i]} · {string.Join(" · ", partes)}";
    }
}
