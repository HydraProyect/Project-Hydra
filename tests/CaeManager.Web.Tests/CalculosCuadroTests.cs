using Bunit;
using CaeManager.Web.Features.CuadroDireccion;
using CaeManager.Web.Features.CuadroDireccion.Components;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>Los cálculos puros del Cuadro de dirección, sus preferencias y los gráficos.</summary>
public class CalculosCuadroTests : BunitContext
{
    private static CeldaSolicitudes Celda(int dia, int hora, double peso) => new(dia, hora, 1, peso);

    // ---------------------------------------------------------------- franja de refuerzo

    [Fact]
    public void La_franja_de_refuerzo_es_la_ventana_de_cuatro_horas_con_mas_peso_y_sus_dos_dias_fuertes()
    {
        IReadOnlyList<CeldaSolicitudes> celdas =
        [
            Celda(1, 9, 20), Celda(1, 10, 20), Celda(2, 10, 15), Celda(3, 11, 5), Celda(5, 16, 10), Celda(5, 8, 1),
        ];

        var franja = CalculosCuadro.FranjaDeRefuerzo(celdas)!;

        franja.HoraInicio.Should().Be(8);
        franja.HoraFin.Should().Be(12);
        franja.PesoPorcentaje.Should().Be(61);
        franja.DiasPico.Should().Equal(1, 2);
    }

    [Fact]
    public void Sin_solicitudes_no_hay_franja()
    {
        CalculosCuadro.FranjaDeRefuerzo([]).Should().BeNull();
    }

    // ---------------------------------------------------------------- rotación, ingreso, picos

    [Theory]
    [InlineData(10, 10, 100, 20.0)]
    [InlineData(0, 0, 100, 0.0)]
    public void La_rotacion_compara_altas_y_bajas_con_la_plantilla_media(int altas, int bajas, int activos, double esperada) =>
        CalculosCuadro.Rotacion(altas, bajas, activos).Should().BeApproximately(esperada, 0.001);

    [Fact]
    public void Sin_plantilla_no_hay_rotacion()
    {
        CalculosCuadro.Rotacion(0, 0, 0).Should().BeNull();
    }

    [Fact]
    public void El_ingreso_por_gestion_no_divide_por_cero()
    {
        CalculosCuadro.IngresoPorGestion(100m, 0).Should().BeNull();
        CalculosCuadro.IngresoPorGestion(100m, null).Should().BeNull();
        CalculosCuadro.IngresoPorGestion(100m, 4).Should().Be(25);
    }

    [Fact]
    public void Los_meses_pico_son_los_tres_de_mas_carga_y_se_ignoran_los_vacios()
    {
        IReadOnlyList<double?> valores = [1, null, 9, 3, 8, 0, 7, 2, null, 1, 1, 1];

        CalculosCuadro.IndicesPico(valores, 3).Should().BeEquivalentTo(new[] { 2, 4, 6 });
    }

    [Fact]
    public void La_estacionalidad_promedia_las_gestiones_por_dia_laborable_de_cada_mes_del_año()
    {
        var resultado = CalculosCuadro.GestionesPorDiaLaborableSegunMes(
            [(2025, 3, 230), (2026, 3, 253), (2026, 4, (int?)null)]);

        resultado[2].Should().BeApproximately((230 / 21.0 + 253 / 22.0) / 2, 0.001);
        resultado[3].Should().BeNull();
        resultado[0].Should().BeNull();
    }

    [Fact]
    public void Los_dias_laborables_cuentan_de_lunes_a_viernes()
    {
        CalculosCuadro.DiasLaborables(2026, 10).Should().Be(22);
        CalculosCuadro.DiasLaborables(2026, 2).Should().Be(20);
    }

    // ---------------------------------------------------------------- concentración, variación, calendario, CSV

    [Fact]
    public void La_concentracion_ordena_de_mayor_a_menor_y_acumula()
    {
        var pareto = CalculosCuadro.Pareto([("B", 20m), ("A", 60m), ("C", 20m), ("Sin", 0m)]);

        pareto.Select(p => p.Nombre).Should().Equal("A", "B", "C");
        pareto.Select(p => p.Acumulado).Should().Equal(60, 80, 100);
        CalculosCuadro.Pareto([("A", 0m)]).Should().BeEmpty();
    }

    [Fact]
    public void La_variacion_no_se_calcula_sobre_un_previo_de_cero()
    {
        CalculosCuadro.Variacion(110, 100).Should().BeApproximately(10, 0.001);
        CalculosCuadro.Variacion(5, 0).Should().BeNull();
    }

    [Fact]
    public void El_calendario_agrupa_por_semanas_de_lunes_a_viernes_y_marca_los_dias_fuera_de_rango()
    {
        var porDia = new Dictionary<DateOnly, int> { [new DateOnly(2026, 10, 7)] = 3 };

        var semanas = CalculosCuadro.Semanas(new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 14), porDia);

        semanas.Should().HaveCount(2);
        semanas[0].Select(d => d.Dia.Day).Should().Equal(5, 6, 7, 8, 9);
        semanas[0].Select(d => d.EnRango).Should().Equal(false, false, true, true, true);
        semanas[0][2].Cantidad.Should().Be(3);
        semanas[1].Select(d => d.Dia.Day).Should().Equal(12, 13, 14, 15, 16);
        semanas[1].Select(d => d.EnRango).Should().Equal(true, true, true, false, false);
    }

    [Fact]
    public void El_CSV_separa_con_punto_y_coma_y_entrecomilla_solo_lo_que_hace_falta()
    {
        var csv = CalculosCuadro.Csv([["Empresa", "Nota"], ["Ebro; S.L.", "dice \"hola\""], ["Sin", "—"]]);

        csv.Should().Be("Empresa;Nota\r\n\"Ebro; S.L.\";\"dice \"\"hola\"\"\"\r\nSin;—\r\n");
    }

    [Theory]
    [InlineData("=SUMA(A1:A2)", "'=SUMA(A1:A2)")]
    [InlineData("+34600111222", "'+34600111222")]
    [InlineData("-5 %", "'-5 %")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("Ebro", "Ebro")]
    public void El_CSV_neutraliza_los_campos_que_una_hoja_de_calculo_leeria_como_formula(string campo, string esperado)
    {
        CalculosCuadro.Csv([["Empresa"], [campo]]).Should().Be($"Empresa\r\n{esperado}\r\n");
    }

    // ---------------------------------------------------------------- preferencias

    [Theory]
    [InlineData("{\"Ocultas\":null,\"Orden\":null,\"AvisosVistos\":null,\"Etiquetas\":true}")]
    [InlineData("{\"Ocultas\":[null],\"Orden\":{\"general\":null,\"otra\":[null,\"a\"]},\"AvisosVistos\":[null]}")]
    [InlineData("null")]
    public void Un_JSON_valido_con_propiedades_a_null_se_lee_sin_romper_nada(string json)
    {
        var leidas = PreferenciasCuadro.Leer(json);

        leidas.Ocultas.Should().NotBeNull().And.NotContainNulls();
        leidas.AvisosVistos.Should().NotBeNull().And.NotContainNulls();
        leidas.Orden.Should().NotBeNull();
        leidas.Ordenar("general", ["a", "b"]).Should().Equal("a", "b");
        leidas.Ordenar("otra", ["a", "b"]).Should().Equal("a", "b");
        leidas.EsVisible("a").Should().BeTrue();
    }

    [Fact]
    public void Las_preferencias_se_serializan_y_un_JSON_roto_deja_los_valores_por_defecto()
    {
        var preferencias = new PreferenciasCuadro { Etiquetas = true };
        preferencias.Alternar("k-ext");
        preferencias.AvisosVistos.Add("2026-10");

        var leidas = PreferenciasCuadro.Leer(preferencias.Serializar());

        leidas.Etiquetas.Should().BeTrue();
        leidas.EsVisible("k-ext").Should().BeFalse();
        leidas.AvisosVistos.Should().Equal("2026-10");
        PreferenciasCuadro.Leer("{ roto").Ocultas.Should().BeEmpty();
        PreferenciasCuadro.Leer(null).Etiquetas.Should().BeFalse();
    }

    [Fact]
    public void El_orden_guardado_descarta_tarjetas_que_ya_no_existen_y_pone_las_nuevas_al_final()
    {
        var preferencias = new PreferenciasCuadro { Orden = { ["general"] = ["c", "vieja", "a"] } };

        preferencias.Ordenar("general", ["a", "b", "c"]).Should().Equal("c", "a", "b");
        preferencias.Ordenar("otra", ["a", "b"]).Should().Equal("a", "b");
    }

    [Fact]
    public void Mover_y_colocar_reordenan_sin_salirse_de_los_extremos()
    {
        var preferencias = new PreferenciasCuadro();
        IReadOnlyList<string> ids = ["a", "b", "c", "d"];

        preferencias.Mover("t", ids, "c", -1);
        preferencias.Ordenar("t", ids).Should().Equal("a", "c", "b", "d");

        preferencias.Mover("t", ids, "a", -5);
        preferencias.Ordenar("t", ids).Should().Equal("a", "c", "b", "d");

        preferencias.Colocar("t", ids, "d", "a");
        preferencias.Ordenar("t", ids).Should().Equal("d", "a", "c", "b");

        preferencias.Alternar("a");
        preferencias.Restablecer("t", ids);
        preferencias.Ordenar("t", ids).Should().Equal(ids);
        preferencias.EsVisible("a").Should().BeTrue();
    }

    // ---------------------------------------------------------------- gráficos

    [Fact]
    public void El_grafico_apila_las_series_y_no_pinta_barras_vacias()
    {
        var cut = Render<GraficoSerie>(ps => ps
            .Add(p => p.Id, "x")
            .Add(p => p.Etiquetas, ["Sep", "Oct"])
            .Add(p => p.Barras, [new SerieGrafico("A", "var(--a)", [10, 0]), new SerieGrafico("B", "var(--b)", [5, 20])]));

        cut.FindAll("rect.gs-barra").Count.Should().Be(3, "la serie A de octubre vale 0");
        cut.FindAll(".gs-leyenda span").Count.Should().Be(2);
        cut.Find(".gs-tip").TextContent.Should().Be("Sep · A: 10 · B: 5");
    }

    [Fact]
    public void El_grafico_resalta_las_barras_pedidas_y_el_compacto_solo_dibuja_la_linea()
    {
        var resaltado = Render<GraficoSerie>(ps => ps
            .Add(p => p.Etiquetas, ["A", "B", "C"])
            .Add(p => p.Barras, [new SerieGrafico("S", "var(--s)", [1, 5, 2])])
            .Add(p => p.Resaltar, new HashSet<int> { 1 }));
        resaltado.FindAll("rect.gs-resaltada").Count.Should().Be(1);

        var compacto = Render<GraficoSerie>(ps => ps
            .Add(p => p.Compacto, true)
            .Add(p => p.Etiquetas, ["A", "B", "C"])
            .Add(p => p.Linea, new SerieGrafico("S", "var(--s)", [1, 5, 2])));
        compacto.FindAll("polyline.gs-linea").Count.Should().Be(1);
        compacto.FindAll("rect").Count.Should().Be(0);
        compacto.FindAll(".gs-zona").Count.Should().Be(0);
    }

    [Fact]
    public void El_grafico_con_series_comparadas_dibuja_una_linea_por_serie_sobre_el_mismo_eje()
    {
        var cut = Render<GraficoSerie>(ps => ps
            .Add(p => p.Etiquetas, ["Ene", "Feb"])
            .Add(p => p.Comparadas, [new SerieGrafico("2025", "var(--a)", [10, 20]), new SerieGrafico("2026", "var(--b)", [30, null])]));

        cut.FindAll("polyline.gs-linea").Count.Should().Be(2);
        cut.FindAll("span.gs-punto").Count.Should().Be(3, "los puntos son HTML sobre el plano para que el SVG estirado no los deforme");
        cut.Find(".gs-zona:last-child .gs-tip").TextContent.Should().Contain("2026: —");
    }

    [Fact]
    public void La_dispersion_pinta_una_burbuja_por_Empresa_con_su_tooltip()
    {
        var cut = Render<GraficoDispersion>(ps => ps
            .Add(p => p.EtiquetaX, "Gestiones")
            .Add(p => p.EtiquetaY, "Euros")
            .Add(p => p.Puntos, [new PuntoDispersion("Ebro", 10, 5, 100, true), new PuntoDispersion("Refri", 30, 20, 400, false)])
            .Add(p => p.Describir, p => $"{p.Nombre} importa"));

        cut.FindAll("g.gd-punto").Count.Should().Be(2);
        cut.Find("g[data-empresa=Ebro] title").TextContent.Should().Be("Ebro importa");
        cut.Markup.Should().Contain("Gestiones").And.Contain("Euros");
    }
}
