using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Contrato del CSS de la pestaña Trabajadores de Centro 360 en una caja estrecha (demo 2026-11-10): a 1024 px la columna
/// principal convive con el panel «Información» y las tres columnas fijas (284 px) dejaban al nombre sin ancho, que se partía
/// letra a letra con filas de 337-437 px. bUnit no aplica CSS: estos tests leen el fichero y fijan las reglas; cómo las pinta un
/// navegador no lo comprueban.
/// </summary>
public class AcordeonAsignacionesCentroEstrechoCssTests
{
    private const string Lista = ".lista-trabajadores-acordeon:not(.lista-trabajadores-acordeon-listado)";

    private static string Css()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;

        directorio.Should().NotBeNull("hay que encontrar la raíz del repositorio para leer el CSS");
        return File.ReadAllText(Path.Combine(directorio!.FullName, "src", "CaeManager.Web", "Features", "Centros", "Components", "AcordeonAsignacionesCentro.razor.css"));
    }

    /// <summary>El interior del bloque <c>@container (...) { ... }</c> con esa condición, con las llaves casadas.</summary>
    private static string Bloque(string css, string condicion)
    {
        var inicio = css.IndexOf($"@container ({condicion}) {{", StringComparison.Ordinal);
        inicio.Should().BeGreaterThanOrEqualTo(0, $"debe existir el bloque @container ({condicion})");
        var abre = css.IndexOf('{', inicio);
        var nivel = 0;
        for (var i = abre; i < css.Length; i++)
        {
            if (css[i] == '{') nivel++;
            else if (css[i] == '}' && --nivel == 0) return css[(abre + 1)..i];
        }

        throw new InvalidOperationException($"El bloque @container ({condicion}) no cierra.");
    }

    private static string Regla(string bloque, string selector)
    {
        var m = Regex.Match(bloque, Regex.Escape(selector) + @"\s*\{([^}]*)\}");
        m.Success.Should().BeTrue($"debe existir la regla «{selector}»");
        return m.Groups[1].Value;
    }

    [Fact]
    public void El_CSS_tiene_las_llaves_casadas()
    {
        var css = Css();

        css.Count(c => c == '{').Should().Be(css.Count(c => c == '}'),
            "una llave sobrante hace que el navegador descarte la regla siguiente");
    }

    [Fact]
    public void En_ancho_la_columna_del_nombre_es_flexible_con_un_minimo_razonable()
    {
        var ancho = Bloque(Css(), "min-width: 641px");

        var rejilla = Regla(ancho, $"{Lista} ::deep .fila-relacion-rejilla");
        rejilla.Should().Contain("grid-template-columns: minmax(16rem, 1fr) var(--ancho-fraccion-trabajador) var(--ancho-estado-trabajador) var(--ancho-acciones-trabajador);");
        Regla(ancho, $"{Lista} ::deep .fila-relacion-rejilla:not(.fila-relacion-compacta) > .fila-relacion-identidad")
            .Should().Contain("grid-column: 1;", "FilaRelacion da a la identidad todas las columnas a <= 767 px");
        Regla(ancho, $"{Lista} ::deep .fila-relacion-nombre").Should().Contain("flex: 0 0 auto").And.Contain("max-width: 100%");
    }

    [Fact]
    public void En_estrecho_la_fila_apila_el_nombre_arriba_y_cifra_estado_y_accion_debajo()
    {
        var estrecho = Bloque(Css(), "max-width: 640px");

        Regla(estrecho, $"{Lista} ::deep .fila-relacion-rejilla").Should().Contain("grid-template-columns: auto auto minmax(0, 1fr);");
        var identidad = Regla(estrecho, $"{Lista} ::deep .fila-relacion-rejilla:not(.fila-relacion-compacta) > .fila-relacion-identidad");
        identidad.Should().Contain("grid-column: 1 / -1;").And.Contain("grid-row: 1;");
        Regla(estrecho, $"{Lista} ::deep .fila-relacion-cifra").Should().Contain("grid-row: 2;");
        Regla(estrecho, $"{Lista} ::deep .fila-relacion-estado").Should().Contain("grid-row: 2;");
        Regla(estrecho, $"{Lista} ::deep .fila-relacion-acciones").Should().Contain("grid-row: 2;").And.Contain("justify-self: end;");
        Regla(estrecho, $"{Lista} ::deep .fila-relacion-titulo").Should().Contain("flex-wrap: wrap;",
            "la Empresa baja bajo el nombre en vez de repartirse el ancho con él");
    }

    [Fact]
    public void En_estrecho_la_Empresa_y_el_motivo_envuelven_en_vez_de_truncarse()
    {
        var estrecho = Bloque(Css(), "max-width: 640px");

        Regex.IsMatch(estrecho, Regex.Escape($"{Lista} .fila-trabajador-empleador") + @"\s*,").Should().BeTrue("la regla cubre también a la Empresa");
        var regla = Regla(estrecho, $"{Lista} .fila-trabajador-resumen");
        regla.Should().Contain("white-space: normal;").And.Contain("overflow: visible;").And.Contain("text-overflow: clip;");
    }

    [Fact]
    public void En_estrecho_el_segundo_nivel_reduce_su_sangria_y_la_celda_suelta_lleva_su_etiqueta()
    {
        var estrecho = Bloque(Css(), "max-width: 640px");

        Regla(estrecho, $"{Lista} ::deep .fila-relacion-desplegado").Should().Contain("padding-left: calc(var(--fila-relleno-x) + var(--space-2));");
        Regla(estrecho, $"{Lista} ::deep .tabla-plataforma-centro").Should().Contain("padding-left: 0;");
        Regla(estrecho, $"{Lista} .fila-plataforma-centro > [data-etiqueta-celda]::before")
            .Should().Contain("content: attr(data-etiqueta-celda)");
    }

    [Fact]
    public void La_rejilla_base_de_cuatro_columnas_sigue_ahi_para_la_Empresa_y_los_listados()
    {
        Css().Should().Contain("grid-template-columns: minmax(0, 1fr) var(--ancho-fraccion-trabajador) var(--ancho-estado-trabajador) var(--ancho-acciones-trabajador);");
    }
}
