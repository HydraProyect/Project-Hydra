using System.Globalization;
using System.Text;

namespace CaeManager.E2ETests.Fidelidad360;

public enum VeredictoFila
{
    /// <summary>Los dos lados dan el mismo valor dentro de la tolerancia.</summary>
    Igual,

    /// <summary>Los dos lados lo miden y difieren más que la tolerancia.</summary>
    Distinto,

    /// <summary>El mockup lo tiene y la ficha no: falta construirlo.</summary>
    AusenteEnFicha,

    /// <summary>La ficha lo tiene y el mockup no.</summary>
    AusenteEnMockup,

    /// <summary>
    /// Color de una pastilla cuyo rótulo solo existe en un lado. No es una diferencia de
    /// diseño sino de datos de ejemplo: se enseña y no cuenta.
    /// </summary>
    SinPareja,
}

public sealed record FilaDiferencia(string Clave, string Mockup, string Ficha, string Tolerancia, VeredictoFila Veredicto)
{
    public bool Cuenta => Veredicto is VeredictoFila.Distinto or VeredictoFila.AusenteEnFicha or VeredictoFila.AusenteEnMockup;
}

public sealed record FilaNorma(string Clave, string Esperado, string Medido, string Tolerancia, bool? Cumple);

public sealed record InformeFidelidad(
    string Pareja, string Tema, IReadOnlyList<FilaDiferencia> Filas, IReadOnlyList<string> NucleoNoObservado,
    Medicion Mockup, Medicion Ficha)
{
    public IReadOnlyList<FilaDiferencia> Diferencias => [.. Filas.Where(f => f.Cuenta)];

    /// <summary>
    /// El instrumento no vio en los dos lados las piezas sin las que una ficha 360 no lo es.
    /// Un informe ciego no es un informe sin diferencias: nunca se lee como verde.
    /// </summary>
    public bool Ciego => NucleoNoObservado.Count > 0;

    public bool SinDiferencias => !Ciego && Diferencias.Count == 0;

    public string ATablaMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"### {Pareja} · tema {Tema}");
        sb.AppendLine();
        if (Ciego)
            sb.AppendLine(CultureInfo.InvariantCulture, $"**INSTRUMENTO CIEGO** — no observado en los dos lados: {string.Join(", ", NucleoNoObservado)}.").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{Diferencias.Count} diferencias en {Filas.Count} magnitudes. Piezas vistas — mockup: {Vistas(Mockup)}; ficha: {Vistas(Ficha)}.");
        sb.AppendLine();
        sb.AppendLine("| Pieza · propiedad | Mockup | Ficha | Tolerancia | Veredicto |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var f in Filas.OrderByDescending(f => f.Cuenta).ThenBy(f => f.Clave, StringComparer.Ordinal))
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {f.Clave} | {f.Mockup} | {f.Ficha} | {f.Tolerancia} | {Rotulo(f.Veredicto)} |");
        return sb.ToString();
    }

    private static string Vistas(Medicion m) => string.Join(", ", m.PiezasVistas.Select(p => $"{p.Key} {p.Value}"));

    private static string Rotulo(VeredictoFila v) => v switch
    {
        VeredictoFila.Igual => "igual",
        VeredictoFila.Distinto => "**DISTINTO**",
        VeredictoFila.AusenteEnFicha => "**AUSENTE EN FICHA**",
        VeredictoFila.AusenteEnMockup => "**AUSENTE EN MOCKUP**",
        _ => "sin pareja (no cuenta)",
    };
}

/// <summary>
/// Compara la medición de una ficha 360 con la de su mockup y devuelve la tabla de
/// diferencias, con la tolerancia aplicada a cada fila. No decide qué lado tiene razón: la
/// fuente de verdad del diseño es el mockup, pero un mockup anterior a una decisión queda
/// por detrás de ella, y para eso está <see cref="Norma360"/>.
/// </summary>
public static class ComparadorFidelidad360
{
    /// <summary>Tolerancia en píxeles de una longitud, salvo las listadas en <see cref="ToleranciasPropias"/>.</summary>
    public const double ToleranciaPx = 0.5;

    /// <summary>Tolerancia por canal (0-255) de un color: absorbe el redondeo de <c>color-mix</c>.</summary>
    public const int ToleranciaCanal = 2;

    /// <summary>
    /// Magnitudes que son posiciones o anchos de maquetación y no métricas de un token:
    /// dependen del reparto del ancho y admiten un píxel o dos de redondeo de subpíxel.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, double> ToleranciasPropias = new Dictionary<string, double>
    {
        ["lateral.width"] = 1,
        ["anillo.width"] = 1,
        ["anillo.height"] = 1,
        ["pastilla-de-fila.dispersion-izquierda"] = 1,
        ["pastilla-de-fila.distancia-al-borde-derecho"] = 2,
    };

    /// <summary>
    /// Lo mínimo que tiene que haberse medido en los dos lados para que la comparación diga
    /// algo: una tarjeta, el lateral y alguna pastilla.
    /// </summary>
    private static readonly string[][] Nucleo =
    [
        ["tarjeta.border-top-color"],
        ["lateral.width"],
        ["pastilla.font-size", "pastilla-de-fila.font-size"],
    ];

    public static InformeFidelidad Comparar(string pareja, string tema, Medicion mockup, Medicion ficha)
    {
        var filas = new List<FilaDiferencia>();
        foreach (var clave in mockup.Magnitudes.Keys.Union(ficha.Magnitudes.Keys).Order(StringComparer.Ordinal))
        {
            var enMockup = mockup.Magnitudes.GetValueOrDefault(clave);
            var enFicha = ficha.Magnitudes.GetValueOrDefault(clave);
            var esColorDeRotulo = clave.StartsWith("pastilla[", StringComparison.Ordinal);

            if (enMockup is null || enFicha is null)
            {
                var veredicto = esColorDeRotulo
                    ? VeredictoFila.SinPareja
                    : enFicha is null ? VeredictoFila.AusenteEnFicha : VeredictoFila.AusenteEnMockup;
                filas.Add(new FilaDiferencia(clave, enMockup?.Valor ?? "—", enFicha?.Valor ?? "—", "—", veredicto));
                continue;
            }

            var (igual, tolerancia) = SonIguales(clave, enMockup, enFicha);
            filas.Add(new FilaDiferencia(clave, enMockup.Valor, enFicha.Valor, tolerancia, igual ? VeredictoFila.Igual : VeredictoFila.Distinto));
        }

        var noObservado = Nucleo
            .Where(alternativas => !alternativas.Any(c => mockup.Magnitudes.ContainsKey(c) && ficha.Magnitudes.ContainsKey(c)))
            .Select(alternativas => string.Join(" o ", alternativas))
            .ToList();

        return new InformeFidelidad(pareja, tema, filas, noObservado, mockup, ficha);
    }

    internal static (bool Igual, string Tolerancia) SonIguales(string clave, Magnitud a, Magnitud b) =>
        SonIguales(clave, a.Tipo, a.Valor, b.Valor);

    internal static (bool Igual, string Tolerancia) SonIguales(string clave, string tipo, string a, string b)
    {
        switch (tipo)
        {
            case "px" when TryNumero(a, out var x) && TryNumero(b, out var y):
                var tolerancia = ToleranciasPropias.GetValueOrDefault(clave, ToleranciaPx);
                return (Math.Abs(x - y) <= tolerancia, $"±{tolerancia.ToString(CultureInfo.InvariantCulture)} px");
            case "color" when TryColor(a, out var c1) && TryColor(b, out var c2):
                return (c1.Zip(c2, (p, q) => Math.Abs(p - q)).All(d => d <= ToleranciaCanal), $"±{ToleranciaCanal} por canal");
            default:
                // Texto, o un lado con varios valores («13 | 11»): solo vale la igualdad exacta.
                return (string.Equals(a, b, StringComparison.Ordinal), "exacta");
        }
    }

    private static bool TryNumero(string valor, out double numero) =>
        double.TryParse(valor, NumberStyles.Float, CultureInfo.InvariantCulture, out numero);

    private static bool TryColor(string valor, out int[] canales)
    {
        canales = [];
        if (valor.Length != 9 || valor[0] != '#')
            return false;
        var leidos = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!int.TryParse(valor.AsSpan(1 + i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out leidos[i]))
                return false;
        }

        canales = leidos;
        return true;
    }
}

/// <summary>
/// Los valores de diseño decididos para las fichas 360 (Chris, 2026-10-08), como afirmaciones
/// sobre un solo lado. Sirve para dos cosas que la comparación con el mockup no da: decir que
/// un mockup anterior a la decisión se quedó atrás, y afirmar el valor aunque no haya mockup.
/// Solo figura lo decidido con cifra; el resto lo dice el mockup.
/// </summary>
public static class Norma360
{
    private sealed record Regla(string Clave, string Tipo, string Esperado, string? Tema = null);

    private static readonly Regla[] Reglas =
    [
        // Pastilla normal: 600 13px/20px con borde. La variante pequeña de Badge no cambia,
        // así que una ficha cuyas filas usen la pequeña no cumple aquí a propósito: lo que
        // la maqueta pone en las filas es la pastilla normal.
        new("pastilla.font-weight", "px", "600"),
        new("pastilla.font-size", "px", "13"),
        new("pastilla.line-height", "px", "20"),
        new("pastilla.border-top-width", "px", "1"),
        new("pastilla-de-fila.font-weight", "px", "600"),
        new("pastilla-de-fila.font-size", "px", "13"),
        new("pastilla-de-fila.line-height", "px", "20"),
        new("pastilla-de-fila.border-top-width", "px", "1"),
        new("pastilla-de-fila.dispersion-izquierda", "px", "0"),
        new("tarjeta.box-shadow", "texto", "none"),
        new("cabecera-identidad.en-tarjeta", "texto", "sí"),
        new("anillo.width", "px", "84"),
        new("anillo.height", "px", "84"),
        new("lateral.width", "px", "300"),
        new("fila-con-problema[peligro].degradado", "texto", "sí"),
        new("fila-con-problema[advertencia].degradado", "texto", "sí"),
        new("fondo-pagina.background-color", "color", "#e9eef4ff", "claro"),
        new("fila-detalle.color", "color", "#46566cff", "claro"),
        new("tarjeta.border-top-color", "color", "#cfd8e3ff", "claro"),
    ];

    /// <summary><c>Cumple</c> es nulo cuando el lado no tiene la pieza: no observado, no «cumple».</summary>
    public static IReadOnlyList<FilaNorma> Evaluar(Medicion medicion, string tema)
    {
        var filas = new List<FilaNorma>();
        foreach (var regla in Reglas.Where(r => r.Tema is null || r.Tema == tema))
        {
            if (!medicion.Magnitudes.TryGetValue(regla.Clave, out var medido))
            {
                filas.Add(new FilaNorma(regla.Clave, regla.Esperado, "—", "—", null));
                continue;
            }

            var (igual, tolerancia) = ComparadorFidelidad360.SonIguales(regla.Clave, regla.Tipo, regla.Esperado, medido.Valor);
            filas.Add(new FilaNorma(regla.Clave, regla.Esperado, medido.Valor, tolerancia, igual));
        }

        return filas;
    }

    public static string ATablaMarkdown(string titulo, IReadOnlyList<FilaNorma> filas)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"### Norma · {titulo}");
        sb.AppendLine();
        sb.AppendLine("| Pieza · propiedad | Decidido | Medido | Tolerancia | Veredicto |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var f in filas)
        {
            var veredicto = f.Cumple switch { true => "cumple", false => "**NO CUMPLE**", null => "no observado" };
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {f.Clave} | {f.Esperado} | {f.Medido} | {f.Tolerancia} | {veredicto} |");
        }

        return sb.ToString();
    }
}
