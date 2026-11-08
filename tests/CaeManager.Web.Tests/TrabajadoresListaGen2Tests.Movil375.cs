using System.Text.RegularExpressions;
using Bunit;
using CaeManager.Domain.Documentos;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lista de Trabajadores a 375 y a 1024 px (revisión 1:1, 2026-11-08): la rejilla de seis pistas pide 856 px (840 con
/// selección múltiple) y la tarjeta mide el viewport menos ~326 px de barra lateral, así que entre 901 y 1199 px se salía por
/// la derecha sin scroll y a 375 px el marco medía 893 px. Se apila como Empresas y Centros (#2063, #2066). El apilado es CSS
/// (bUnit no calcula maquetación): aquí se fija lo que lo sostiene en el marcado y en la hoja fuente; el ancho real se midió
/// en el navegador.
/// </summary>
public partial class TrabajadoresListaGen2Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task La_fila_conserva_el_orden_de_sus_celdas_y_siempre_lleva_la_celda_del_chevron(bool seleccionMultiple)
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Trabajador("Ana", "Alonso") } });
        if (seleccionMultiple)
            await AlternarSeleccionMultiple(cut);

        var fila = cut.Find(".tarjeta-fila-acordeon-cabecera");
        string[] esperadas =
        [
            .. seleccionMultiple ? new[] { "seleccion-trabajador" } : [],
            "celda-chevron-trabajador", "celda-identidad-trabajador", "celda-empresa-trabajador",
            "col-registrados-vigentes", "celda-estado-trabajador", "tarjeta-fila-acordeon-acciones"
        ];
        fila.Children.Select(c => esperadas.FirstOrDefault(c.ClassList.Contains)).Should().Equal(esperadas);
    }

    [Fact]
    public void El_boton_del_chevron_de_la_fila_con_incidencias_lleva_la_misma_clase_de_celda()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Almacen = { ConDesglose(Trabajador("Ana", "Alonso"), 3, 2, IncidenciaDe("Seguro de responsabilidad civil", EstadoDocumento.Vencido, -10)) }
        });

        var fila = cut.Find(".tarjeta-fila-acordeon-cabecera");
        fila.Children[0].ClassList.Should().Contain(["boton-expandir-fila", "celda-chevron-trabajador"],
            "sin esa clase el botón perdería los 24 px fijos del apilado y Empresa, Registrados vigentes y Estado quedarían desalineados");
        fila.Children[0].TagName.Should().Be("BUTTON");
    }

    [Fact]
    public void La_casilla_de_seleccion_mide_16_px_para_que_cuadre_la_sangria_de_64_px_del_apilado()
    {
        Regex.IsMatch(CssListaTrabajadores(), @"\.seleccion-trabajador[^{]*input\[type=""checkbox""\]\s*\{[^}]*width\s*:\s*16px[^}]*height\s*:\s*16px[^}]*margin\s*:\s*0")
            .Should().BeTrue();
    }

    [Fact]
    public async Task La_cabecera_conserva_la_casilla_de_pagina_y_los_tres_rotulos_que_ordenan()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Trabajador("Ana", "Alonso") } });
        await AlternarSeleccionMultiple(cut);

        var cabecera = cut.Find(".cabecera-columnas-trabajadores");
        cabecera.QuerySelectorAll(".seleccion-trabajador input[type=checkbox]").Length.Should().Be(1);
        cabecera.QuerySelectorAll(".cabecera-orden-trabajador").Select(b => b.TextContent.Trim())
            .Should().HaveCount(3).And.Contain(t => t.StartsWith("Trabajador"))
            .And.Contain(t => t.StartsWith("Empresa")).And.Contain(t => t.StartsWith("Estado"));
        cabecera.QuerySelectorAll(".hueco-cabecera-trabajador").Length.Should().Be(2, "los huecos del chevron y de las acciones se ocultan al apilar");
    }

    // ------------------------------------------------- hoja de estilos (bUnit no calcula maquetación)

    private static string CssListaTrabajadores()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio");
        var css = File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Features", "Trabajadores", "Pages", "Trabajadores.razor.css"));
        return Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
    }

    private static string BloqueMovilTrabajadores()
    {
        var css = CssListaTrabajadores();
        var inicio = css.IndexOf("@media (max-width: 1199px) {", StringComparison.Ordinal);
        inicio.Should().BeGreaterThanOrEqualTo(0, "la lista tiene su bloque para pantalla estrecha");
        var abre = css.IndexOf('{', inicio);
        var profundidad = 0;
        for (var i = abre; i < css.Length; i++)
        {
            if (css[i] == '{') profundidad++;
            else if (css[i] == '}' && --profundidad == 0) return css[(abre + 1)..i];
        }
        throw new InvalidOperationException("Bloque @media sin cerrar.");
    }

    [Fact]
    public void A_375_px_la_fila_y_la_cabecera_dejan_de_ser_una_rejilla_de_seis_pistas_y_se_apilan()
    {
        var movil = BloqueMovilTrabajadores();

        Regex.IsMatch(movil, @"\.marco-lista-trabajadores \.rejilla-trabajadores\s*\{[^}]*display\s*:\s*flex[^}]*flex-wrap\s*:\s*wrap")
            .Should().BeTrue("la rejilla de escritorio mide más de 800 px: en móvil es una caja flexible que envuelve");
        Regex.IsMatch(movil, @"\.tarjeta-listado-contenido > \.marco-lista-trabajadores\s*\{[^}]*min-width\s*:\s*0")
            .Should().BeTrue("lista-design.css da 860 px mínimos al marco por debajo de 900 px: sin quitarlos la tarjeta desplaza en horizontal");
        Regex.IsMatch(movil, @"\.celda-empresa-trabajador,[^{]*\.col-registrados-vigentes,[^{]*\.celda-estado-trabajador\s*\{[^}]*order\s*:\s*2[^}]*flex\s*:\s*1 1 100%[^}]*padding-left\s*:\s*36px")
            .Should().BeTrue("Empresa, Registrados vigentes y Estado ocupan cada uno su línea, tras el icono 360, con la sangría del chevron");
        Regex.IsMatch(movil, @"\.celda-identidad-trabajador\s*\{[^}]*min-width\s*:\s*0")
            .Should().BeTrue("un nombre largo no estira la fila");
        Regex.IsMatch(movil, @":not\(\.seleccion-trabajador\):not\(\.cabecera-orden-trabajador\)\s*\{[^}]*display\s*:\s*none")
            .Should().BeTrue("la cabecera oculta lo que no actúa y conserva solo la casilla de página y los rótulos que ordenan");
        const string Seleccion = @"\.rejilla-trabajadores-seleccion\.tarjeta-fila-acordeon-cabecera > ";
        Regex.IsMatch(movil, Seleccion + @"\.celda-empresa-trabajador,[^{]*" + Seleccion + @"\.col-registrados-vigentes,[^{]*" + Seleccion
                + @"\.celda-estado-trabajador\s*\{[^}]*padding-left\s*:\s*64px")
            .Should().BeTrue("con selección múltiple las tres celdas suman la casilla a la sangría");
    }

    [Fact]
    public void El_apilado_llega_hasta_1199_px_y_las_dos_rejillas_de_escritorio_caben_justo_por_encima()
    {
        var css = CssListaTrabajadores();
        css.Should().Contain("@media (max-width: 1199px) {")
            .And.NotMatchRegex(@"@media[^{]*max-width\s*:\s*900px", "el corte de 900 px dejaba sin apilar la franja de 901 a 1199 px, donde la rejilla desbordaba");

        static int Minimo(string pistas)
        {
            var anchos = Regex.Matches(pistas, @"minmax\((\d+)px|(?<![\d(])(\d+)px")
                .Select(m => int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)).ToList();
            return anchos.Sum() + (anchos.Count - 1) * 16 + 32;
        }

        var fuera = css.Replace(BloqueMovilTrabajadores(), string.Empty);
        var normal = Regex.Match(fuera, @"\.rejilla-trabajadores\s*\{[^}]*grid-template-columns:\s*([^;]+);").Groups[1].Value;
        var seleccion = Regex.Match(fuera, @"\.rejilla-trabajadores-seleccion\s*\{[^}]*grid-template-columns:\s*([^;]+);").Groups[1].Value;
        normal.Should().NotBeNullOrEmpty();
        seleccion.Should().NotBeNullOrEmpty();
        const int TarjetaA1200ConBarra = 1200 - 326 - 15;
        Minimo(normal).Should().BeLessThanOrEqualTo(TarjetaA1200ConBarra);
        Minimo(seleccion).Should().BeLessThanOrEqualTo(TarjetaA1200ConBarra);
    }
}
