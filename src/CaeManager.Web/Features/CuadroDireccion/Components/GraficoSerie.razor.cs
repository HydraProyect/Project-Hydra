using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.CuadroDireccion.Components;

/// <summary>Una serie del gráfico: su nombre, el color (variable CSS del diseño) y un valor por etiqueta; null = sin dato.</summary>
public sealed record SerieGrafico(string Nombre, string Color, IReadOnlyList<double?> Valores);

public sealed record BarraGeometria(double X, double Y, double Ancho, double Alto, string Color, bool Resaltada, string? Valor);

public sealed record PuntoGeometria(double X, double Y);

public sealed record LineaGeometria(string Color, string Texto, IReadOnlyList<PuntoGeometria> Puntos);

/// <summary>Una marca del eje Y: su fracción de la altura (0 abajo, 1 arriba) y el valor ya formateado.</summary>
public sealed record MarcaEje(double Fraccion, string Texto);

public partial class GraficoSerie : ComponentBase
{
    /// <summary>A partir de cuántas posiciones el rótulo del eje X se salta de dos en dos para no apretarse.</summary>
    private const int PosicionesParaSaltarUna = 6;

    [Parameter] public string Id { get; set; } = string.Empty;
    [Parameter] public string EtiquetaAria { get; set; } = string.Empty;
    [Parameter] public IReadOnlyList<string> Etiquetas { get; set; } = [];

    /// <summary>Series apiladas en barras, de abajo arriba.</summary>
    [Parameter] public IReadOnlyList<SerieGrafico> Barras { get; set; } = [];

    /// <summary>Línea opcional con su propio eje.</summary>
    [Parameter] public SerieGrafico? Linea { get; set; }

    /// <summary>Varias líneas que comparten el mismo eje (una por año en la Comparativa anual).</summary>
    [Parameter] public IReadOnlyList<SerieGrafico> Comparadas { get; set; } = [];

    /// <summary>Lo que dice el minigráfico con menos de dos puntos: una línea de un solo punto no es una tendencia.</summary>
    [Parameter] public string? TextoSinTendencia { get; set; }

    /// <summary>Color de cada barra por posición (la concentración pinta distinto el primer 50 %); sin él, el de la serie.</summary>
    [Parameter] public IReadOnlyList<string>? ColoresBarra { get; set; }

    /// <summary>Leyenda propia cuando los colores no son los de las series (p. ej. «Primer 50 %» y «Resto»).</summary>
    [Parameter] public IReadOnlyList<SerieGrafico>? LeyendaPropia { get; set; }

    /// <summary>Descripción de cada posición para el tooltip cuando el rótulo del eje es solo un número de orden.</summary>
    [Parameter] public IReadOnlyList<string>? Descripciones { get; set; }

    /// <summary>Máximo fijo del eje de la línea (100 % en un acumulado) y las marcas que se rotulan en él.</summary>
    [Parameter] public double? MaximoFijoLinea { get; set; }
    [Parameter] public IReadOnlyList<double>? MarcasFijasLinea { get; set; }

    /// <summary>Línea de referencia discontinua sobre el eje de la línea (el 80 % de la concentración).</summary>
    [Parameter] public double? ReferenciaLinea { get; set; }

    [Parameter] public bool MostrarValores { get; set; }
    [Parameter] public bool Compacto { get; set; }

    /// <summary>Posiciones de las barras que se resaltan (los meses pico).</summary>
    [Parameter] public IReadOnlySet<int>? Resaltar { get; set; }

    [Parameter] public string ColorResaltado { get; set; } = "var(--color-warning-500)";
    [Parameter] public Func<double, string> FormatoBarra { get; set; } = v => v.ToString("N0", CuadroDireccionUi.Castellano);
    [Parameter] public Func<double, string> FormatoLinea { get; set; } = v => v.ToString("N0", CuadroDireccionUi.Castellano);

    private double Ancho => Compacto ? 120 : 640;
    private double Alto => Compacto ? 32 : 200;
    private double Margen => Compacto ? 2 : 0;
    private double Hueco => Etiquetas.Count == 0 ? 0 : (Ancho - 2 * Margen) / Etiquetas.Count;
    private string ColorLinea => Linea?.Color ?? "var(--color-warning-500)";
    private double PorcentajeAncho => Etiquetas.Count == 0 ? 0 : Math.Round(100.0 / Etiquetas.Count, 3);

    private IReadOnlyList<SerieGrafico> Leyenda => LeyendaPropia ?? [.. Barras, .. Comparadas, .. (Linea is null ? [] : new[] { Linea })];

    private static string Texto(double valor) => valor.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Posición CSS (izquierda y abajo, en %) de un punto de coordenadas del plano SVG.</summary>
    private string Posicion(double x, double y) =>
        $"left:{Texto(x / Ancho * 100)}%;bottom:{Texto((Alto - y) / Alto * 100)}%";

    private IEnumerable<int> Posiciones => Enumerable.Range(0, Etiquetas.Count);

    /// <summary>Con más de seis posiciones se rotula una de cada dos, con el rótulo corto: el texto es de 11 px y no cabe junto.</summary>
    private IEnumerable<int> PosicionesConEtiqueta
    {
        get
        {
            var salto = Etiquetas.Count > PosicionesParaSaltarUna ? 2 : 1;
            return Posiciones.Where(i => i % salto == (Etiquetas.Count - 1) % salto);
        }
    }

    /// <summary>«Oct 2026» sale como «Oct 26» en el eje cuando hay muchos meses; el resto de rótulos no cambia.</summary>
    private string EtiquetaEje(int i)
    {
        var etiqueta = Etiquetas[i];
        if (Etiquetas.Count <= PosicionesParaSaltarUna) return etiqueta;
        var partes = etiqueta.Split(' ');
        return partes.Length == 2 && partes[1].Length == 4 && int.TryParse(partes[1], out _) ? $"{partes[0]} {partes[1][2..]}" : etiqueta;
    }

    /// <summary>Un minigráfico con menos de dos puntos con dato no dibuja una línea.</summary>
    private bool SinTendencia => Compacto && (Linea is null || Linea.Valores.Count(v => v is not null) < 2);

    /// <summary>Con barras y línea a la vez, la línea lleva su propio eje a la derecha (gestiones frente a euros).</summary>
    private bool HayEjeDerecho => !Compacto && Barras.Count > 0 && Linea is not null && MaximoLinea > 0;

    private IReadOnlyList<MarcaEje> MarcasEjeDerecho
    {
        get
        {
            if (!HayEjeDerecho) return [];
            if (MarcasFijasLinea is { Count: > 0 } fijas)
                return fijas.Select(m => new MarcaEje(m / EjeMaximoLinea, FormatoLinea(m))).ToList();
            var (eje, divisiones) = MaximoRedondeado(MaximoLinea);
            return Enumerable.Range(0, divisiones + 1)
                .Select(d => new MarcaEje((double)d / divisiones, FormatoLinea(eje * d / divisiones)))
                .ToList();
        }
    }

    /// <summary>El máximo del eje de la línea: el suyo propio con barras, el único del gráfico sin ellas.</summary>
    private double EjeMaximoLinea => MaximoFijoLinea ?? (Barras.Count > 0 ? (MaximoLinea > 0 ? MaximoRedondeado(MaximoLinea).Maximo : 1) : EjeMaximo);
    private string PorcentajeIzquierda(int i) => Texto(i * PorcentajeAncho);

    private double CentroDe(int i) => Margen + Hueco * (i + 0.5);

    private double TotalApilado(int i) => Barras.Sum(s => s.Valores.ElementAtOrDefault(i) ?? 0);

    private double MaximoBarras => Etiquetas.Count == 0 ? 0 : Enumerable.Range(0, Etiquetas.Count).Max(TotalApilado);

    private double MaximoComparadas =>
        Comparadas.SelectMany(s => s.Valores).Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(0).Max();

    private double MaximoLinea =>
        Linea is null ? 0 : Linea.Valores.Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(0).Max();

    /// <summary>
    /// El máximo del eje Y, redondeado a 1, 2 o 5 por una potencia de diez: las barras (o las líneas comparadas, o la
    /// línea sola) llegan como mucho a él, y las marcas caen en cifras redondas.
    /// </summary>
    private double EjeMaximo
    {
        get
        {
            var maximo = MaximoBarras > 0 ? MaximoBarras : MaximoComparadas > 0 ? MaximoComparadas : MaximoLinea;
            return maximo <= 0 ? 1 : MaximoRedondeado(maximo).Maximo;
        }
    }

    private static (double Maximo, int Divisiones) MaximoRedondeado(double maximo)
    {
        var potencia = Math.Pow(10, Math.Floor(Math.Log10(maximo)));
        var fraccion = maximo / potencia;
        var paso = fraccion <= 1 ? 1 : fraccion <= 2 ? 2 : fraccion <= 5 ? 5 : 10;
        return (paso * potencia, paso == 2 ? 4 : 5);
    }

    /// <summary>Las marcas del eje Y, de 0 al máximo, con su rejilla. Sin dato alguno solo queda la línea base.</summary>
    private IReadOnlyList<MarcaEje> MarcasEjeY
    {
        get
        {
            if (Compacto) return [];

            var maximo = MaximoBarras > 0 ? MaximoBarras : MaximoComparadas > 0 ? MaximoComparadas : MaximoLinea;
            if (maximo <= 0) return [new MarcaEje(0, "0")];

            var (eje, divisiones) = MaximoRedondeado(maximo);
            var formato = Barras.Count > 0 || Comparadas.Count > 0 ? FormatoBarra : FormatoLinea;
            return Enumerable.Range(0, divisiones + 1)
                .Select(d => new MarcaEje((double)d / divisiones, formato(eje * d / divisiones)))
                .ToList();
        }
    }

    private IReadOnlyList<BarraGeometria> GeometriaBarras
    {
        get
        {
            var maximo = MaximoBarras;
            if (Compacto || maximo <= 0) return [];

            var eje = EjeMaximo;
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
                    var alto = valor / eje * Alto;
                    acumulado += alto;
                    var resaltada = Resaltar?.Contains(i) == true;
                    resultado.Add(new BarraGeometria(
                        x, Alto - acumulado, ancho, alto,
                        resaltada ? ColorResaltado : Barras.Count == 1 && ColoresBarra?.ElementAtOrDefault(i) is { } colorBarra ? colorBarra : serie.Color, resaltada,
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

            var puntos = new List<PuntoGeometria>();
            if (Compacto)
            {
                var maximo = conDato.Max();
                var minimo = conDato.Min();
                var rango = maximo - minimo;
                for (var i = 0; i < valores.Count; i++)
                {
                    if (valores[i] is not { } valor) continue;
                    var fraccion = rango <= 0 ? 0.5 : (valor - minimo) / rango;
                    var x = Margen + (Ancho - 2 * Margen) * i / Math.Max(valores.Count - 1, 1);
                    puntos.Add(new PuntoGeometria(x, Alto - 3 - fraccion * (Alto - 6)));
                }
            }
            else
            {
                var eje = EjeMaximoLinea;
                for (var i = 0; i < valores.Count; i++)
                {
                    if (valores[i] is not { } valor) continue;
                    puntos.Add(new PuntoGeometria(CentroDe(i), Alto - valor / eje * Alto));
                }
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

            var eje = EjeMaximo;
            var resultado = new List<LineaGeometria>();
            foreach (var serie in Comparadas)
            {
                var puntos = new List<PuntoGeometria>();
                for (var i = 0; i < serie.Valores.Count; i++)
                {
                    if (serie.Valores[i] is not { } valor) continue;
                    puntos.Add(new PuntoGeometria(CentroDe(i), Alto - valor / eje * Alto));
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
        return $"{Descripciones?.ElementAtOrDefault(i) ?? Etiquetas[i]} · {string.Join(" · ", partes)}";
    }
}
