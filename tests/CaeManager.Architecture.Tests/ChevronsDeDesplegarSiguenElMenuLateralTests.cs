using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Regla visual de Chris (2026-10-18): todas las flechas de desplegar siguen el patrón del menú lateral
/// (<c>NavMenu.razor.css</c>, icono <c>chevron</c> que apunta hacia abajo): <b>cerrado</b> gira a la derecha
/// (<c>rotate(-90deg)</c>), <b>abierto</b> apunta hacia abajo (sin giro), con la transición de expansión
/// (<c>--motion-transition</c> y <c>--ease-fluid</c>). Antes unas giraban a la izquierda (<c>rotate(90deg)</c>) y otras
/// hacia arriba (<c>rotate(180deg)</c>).
///
/// <para>
/// Fuera de alcance, con motivo: los chevrons de <b>menús desplegables</b> (selector de Cliente activo, campo de
/// selección avanzada, menú de acciones, franja de estado), que se abren sobre la página en vez de desplegar un bloque en
/// su sitio, y las flechas direccionales (calendario, ordenar). No tienen un estado «plegado/desplegado» de contenido.
/// </para>
/// </summary>
public class ChevronsDeDesplegarSiguenElMenuLateralTests
{
    private const string Transicion = @"transition:\s*transform\s+var\(--motion-transition\)\s+var\(--ease-fluid\)";

    /// <summary>Fichero de estilos, selector que lleva la transición, selector del estado cerrado y del abierto (si es otra regla).</summary>
    public static TheoryData<string, string, string, string?> Chevrons => new()
    {
        { "src/CaeManager.Web/Components/Layout/NavMenu.razor.css", ".nav-grupo-titulo ::deep .icono", ".nav-grupo-detalle:not([open]) .nav-grupo-titulo ::deep .icono", null },
        { "src/CaeManager.Web/wwwroot/css/list-page.css", ".boton-expandir-fila", ".boton-expandir-fila", ".boton-expandir-fila-abierta" },
        { "src/CaeManager.Web/wwwroot/css/list-page.css", ".grupo-lista-chevron", ".grupo-lista-chevron", ".grupo-lista-cabecera-abierta .grupo-lista-chevron" },
        { "src/CaeManager.Web/Components/DesignSystem/FilaRelacion.razor.css", ".fila-relacion-desplegar", ".fila-relacion-desplegar", @".fila-relacion-desplegar[aria-expanded=""true""]" },
        { "src/CaeManager.Web/Components/DesignSystem/SeccionColapsable.razor.css", ".seccion-colapsable-chevron", ".seccion-colapsable-chevron", ".seccion-colapsable-chevron-abierta" },
        { "src/CaeManager.Web/Features/Vehiculos/Pages/Vehiculos.razor.css", ".boton-expandir-incidencias", ".boton-expandir-incidencias", ".boton-expandir-incidencias[aria-expanded=true]" },
        { "src/CaeManager.Web/Features/Bandeja/Components/GrupoCola.razor.css", ".grupo-cola-chevron", ".grupo-cola-chevron", ".grupo-cola-chevron-abierta" },
        { "src/CaeManager.Web/Features/Alertas/Pages/Alertas.razor.css", ".alertas-grupo-chevron", ".alertas-grupo-chevron-cerrado", null },
        { "src/CaeManager.Web/Features/Bandeja/Pages/MiTrabajo.razor.css", ".mi-trabajo-chevron", ".mi-trabajo-chevron-plegado", null },
        { "src/CaeManager.Web/Features/Comunicaciones/Pages/Bandeja.razor.css", ".bandeja-grupo-caret", @".bandeja-grupo-cabecera[aria-expanded=""false""] .bandeja-grupo-caret", null },
        { "src/CaeManager.Web/Features/Visitas/Pages/VisitaDetalle.razor.css", ".visita360-grupo-plegar", @".visita360-grupo-plegar[aria-expanded=""false""]", null },
    };

    [Theory]
    [MemberData(nameof(Chevrons))]
    public void El_chevron_gira_a_la_derecha_cerrado_apunta_abajo_abierto_y_usa_la_transicion_del_menu_lateral(
        string fichero, string selectorTransicion, string selectorCerrado, string? selectorAbierto)
    {
        var css = File.ReadAllText(Path.Combine(RaizDelRepositorio(), fichero));

        Regla(css, selectorTransicion).Should().MatchRegex(Transicion,
            $"«{selectorTransicion}» anima el giro como el chevron del menú lateral");
        Regla(css, selectorCerrado).Should().Contain("rotate(-90deg)",
            $"«{selectorCerrado}» es el estado cerrado: la flecha apunta a la derecha, como en el menú lateral");

        if (selectorAbierto is not null)
        {
            Regla(css, selectorAbierto).Should().Contain("rotate(0deg)",
                $"«{selectorAbierto}» es el estado abierto: la flecha apunta hacia abajo");
        }

        foreach (var selector in new[] { selectorTransicion, selectorCerrado, selectorAbierto }.OfType<string>())
        {
            Regla(css, selector).Should().NotMatchRegex(@"rotate\((?!-90deg\)|0deg\))",
                $"«{selector}» no puede girar la flecha a la izquierda (90deg) ni hacia arriba (180deg)");
        }
    }

    /// <summary>
    /// El cuerpo de la regla cuyo selector es exactamente <paramref name="selector"/> (empieza línea y va seguido de
    /// <c>{</c>, así que ignora las reglas con selector agrupado con comas). Si hay varias, las junta.
    /// </summary>
    private static string Regla(string css, string selector)
    {
        var cuerpos = Regex.Matches(css, @"(?m)^[ \t]*" + Regex.Escape(selector) + @"[ \t]*\{([^}]*)\}")
            .Select(m => m.Groups[1].Value)
            .ToList();
        cuerpos.Should().NotBeEmpty($"debe existir una regla con el selector «{selector}»");
        return string.Join("\n", cuerpos);
    }

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("No se encontró CaeManager.slnx subiendo desde el directorio de pruebas.");
    }
}
