using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Toda fila de las fichas 360 (<c>.fila-relacion</c>) responde al cursor, y la fila con problema
/// conserva su tinte al hacerlo.
///
/// <para>
/// <b>Por qué hace falta.</b> Defecto visto el 2026-10-09: solo la fila teñida tenía regla de
/// <c>:hover</c>. La regla que lo corrige y las del tinte tienen la misma especificidad, así que lo
/// único que hace que el tinte gane al pasar el cursor es el <b>orden</b> en el fichero y que la
/// exclusión del bloque desplegado vaya dentro de <c>:where()</c>. Sin este test, mover la regla o
/// sacar la exclusión de <c>:where()</c> dejaría la fila con problema en gris al pasar el cursor, con
/// todo lo demás en verde.
/// </para>
///
/// <para>
/// <b>Contrato efectivo.</b> Lee el texto de <c>FilaRelacion.razor.css</c>: no renderiza nada ni
/// calcula la cascada. No ve una regla de otro fichero que alcance la fila, ni comprueba que el
/// resaltado se vea (eso se mira en el navegador, en tema claro y oscuro).
/// </para>
/// </summary>
public sealed partial class FilaRespondeAlCursorTests
{
    private static readonly string Css = File.ReadAllText(Path.Combine(
        Raiz(), "src", "CaeManager.Web", "Components", "DesignSystem", "FilaRelacion.razor.css"));

    [Fact]
    public void La_fila_sin_tinte_tiene_regla_de_cursor_con_el_token_de_resaltado()
    {
        var regla = ReglaDeCursor().Match(Css);

        regla.Success.Should().BeTrue("la fila sin tinte tiene que responder al cursor");
        regla.Groups["declaraciones"].Value.Should().Contain("background-color: var(--color-surface-hover)");
    }

    [Fact]
    public void La_regla_de_cursor_no_gana_al_tinte_de_la_fila_con_problema()
    {
        var regla = ReglaDeCursor().Match(Css);
        regla.Success.Should().BeTrue("sin la regla de cursor, lo de abajo no dice nada");

        // Control positivo: si no se encuentran las dos reglas del tinte, la comparación de posiciones no mide nada.
        var tintes = ReglaDeColorDelTinte().Matches(Css);
        tintes.Should().HaveCount(2, "la fila con problema tiene un color por tono: peligro y advertencia");

        tintes.Select(t => t.Index).Should().OnlyContain(posicion => posicion > regla.Index,
            "con la misma especificidad gana la regla que va después: el tinte tiene que ir después del resaltado");

        // Lo que quede fuera de :where() suma especificidad y haría ganar al resaltado sobre el tinte.
        var selector = regla.Groups["selector"].Value;
        Regex.Replace(selector, @":where\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)", string.Empty).Trim()
            .Should().Be(".fila-relacion:hover",
                "fuera de :where() el selector solo puede ser .fila-relacion:hover");
    }

    [Fact]
    public void La_transicion_del_resaltado_se_apaga_con_movimiento_reducido()
    {
        Css.Should().MatchRegex(@"\.fila-relacion\s*\{[^}]*transition:\s*background-color\s+120ms");
        Css.Should().MatchRegex(
            @"@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{\s*\.fila-relacion\s*\{\s*transition:\s*none;");
    }

    /// <summary>
    /// Segundo nivel (decisión de Chris del 2026-11-08): la subfila con problema dentro de una fila
    /// desplegada —la de FilaRelacion o la de la tarjeta-fila-acordeon de los listados— no lleva
    /// degradado, sino una raya lateral de 3 px; el nivel 1 conserva el tinte. Sin este test, borrar
    /// las reglas anidadas o quitarles el ancestro deja la suite en verde.
    /// </summary>
    [Theory]
    [InlineData("peligro", "--color-danger-500")]
    [InlineData("advertencia", "--color-warning-500")]
    public void La_subfila_desplegada_marca_el_estado_con_una_raya_y_no_con_fondo(string tono, string token)
    {
        foreach (var ancestro in new[] { @"\.fila-relacion-desplegado", @"\.tarjeta-fila-acordeon-contenido" })
        {
            Css.Should().MatchRegex(
                ancestro + @"\s+\.fila-relacion\[data-tono\]\s*[,{][^}]*background-image:\s*none",
                $"la subfila bajo {ancestro} no lleva el degradado del nivel 1");
            Css.Should().MatchRegex(
                ancestro + $@"\s+\.fila-relacion\[data-tono=""{tono}""\]\s*[,{{][^}}]*box-shadow:\s*inset 3px 0 0 var\({token}\)",
                $"la subfila {tono} bajo {ancestro} lleva la raya lateral");
        }
    }

    [Fact]
    public void El_nivel_1_conserva_el_tinte()
    {
        // Una regla cuyo selector empiece por .fila-relacion[data-tono (sin ancestro) no puede anular el degradado.
        SinAncestroQueAnulaElDegradado().IsMatch(Css).Should().BeFalse(
            "solo la subfila desplegada pierde el degradado; la fila de nivel 1 lo conserva");
    }

    [Fact]
    public void La_transicion_de_la_subfila_desplegada_se_apaga_con_movimiento_reducido()
    {
        Css.Should().MatchRegex(
            @"@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{\s*\.fila-relacion-desplegado \.fila-relacion\[data-tono\],\s*\.tarjeta-fila-acordeon-contenido \.fila-relacion\[data-tono\]\s*\{\s*transition:\s*none;");
    }

    [GeneratedRegex(@"(?m)^\.fila-relacion\[data-tono[^{]*\{[^}]*background-image:\s*none")]
    private static partial Regex SinAncestroQueAnulaElDegradado();

    [GeneratedRegex(@"(?<selector>\.fila-relacion:hover[^{,]*)\{(?<declaraciones>[^}]*)\}")]
    private static partial Regex ReglaDeCursor();

    [GeneratedRegex(@"\.fila-relacion\[data-tono=""[a-z]+""\]\s*\{[^}]*background-color:")]
    private static partial Regex ReglaDeColorDelTinte();

    private static string Raiz()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;
        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
