using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.CuadroDireccion.Components;

/// <summary>Una Empresa en la dispersión: abscisa, ordenada y tamaño (la facturación).</summary>
public sealed record PuntoDispersion(string Nombre, double X, double Y, double Tamano, bool Destacado);

public sealed record BurbujaGeometria(string Nombre, double X, double Y, double Radio, string Color, string Titulo);

public partial class GraficoDispersion : ComponentBase
{
    [Parameter] public string Id { get; set; } = string.Empty;
    [Parameter] public string EtiquetaAria { get; set; } = string.Empty;
    [Parameter] public string EtiquetaX { get; set; } = string.Empty;
    [Parameter] public string EtiquetaY { get; set; } = string.Empty;
    [Parameter] public IReadOnlyList<PuntoDispersion> Puntos { get; set; } = [];

    /// <summary>Texto que describe cada punto en su tooltip.</summary>
    [Parameter] public Func<PuntoDispersion, string> Describir { get; set; } = p => p.Nombre;

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

    private double MaximoX => Puntos.Count == 0 ? 1 : Math.Max(Puntos.Max(p => p.X), 1);
    private double MaximoY => Puntos.Count == 0 ? 1 : Math.Max(Puntos.Max(p => p.Y), 1);
    private double PosicionY(double y) => 230 - y / MaximoY * 200;

    private static MarkupString TextoSvg(string clase, double x, double y, string contenido, string ancla) =>
        new($"<text class=\"{clase}\" x=\"{Texto(x)}\" y=\"{Texto(y)}\" text-anchor=\"{ancla}\">{System.Net.WebUtility.HtmlEncode(contenido)}</text>");

    private static string Texto(double valor) => valor.ToString("0.##", CultureInfo.InvariantCulture);

    private IReadOnlyList<BurbujaGeometria> Geometria
    {
        get
        {
            if (Puntos.Count == 0) return [];
            var maximoTamano = Math.Max(Puntos.Max(p => p.Tamano), 1);
            return Puntos
                .Select(p => new BurbujaGeometria(
                    p.Nombre,
                    40 + p.X / MaximoX * 560,
                    PosicionY(p.Y),
                    6 + p.Tamano / maximoTamano * 14,
                    p.Destacado ? "var(--color-danger-500)" : "var(--color-primary-500)",
                    Describir(p)))
                .ToList();
        }
    }
}
