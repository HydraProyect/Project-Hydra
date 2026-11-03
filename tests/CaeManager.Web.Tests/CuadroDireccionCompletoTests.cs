using Bunit;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Dashboard;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.CuadroDireccion;
using CaeManager.Web.Features.CuadroDireccion.Components;
using FluentAssertions;
using CuadroDireccionPagina = CaeManager.Web.Features.CuadroDireccion.Pages.CuadroDireccion;

namespace CaeManager.Web.Tests;

/// <summary>
/// El resto del Cuadro de dirección contra su maqueta: Gestiones del mes, Facturación, Empresas con sus columnas,
/// Demanda, Comunicaciones, Vacaciones, Comparativa anual, y las preferencias de la pantalla. Lo que ningún modelo
/// guarda todavía se afirma como estado vacío, no como cifra.
/// </summary>
public partial class CuadroDireccionTests
{
    private static SerieCuadroDireccionDto SerieDe(
        Func<int, CifrasCuadroDireccionDto> cifras, IReadOnlyList<LineaFacturacionCuadroDto>? facturacion = null, int meses = 6, int terminaEnAnyo = 0)
    {
        var hoy = DiaDeNegocio.Hoy();
        var ultimo = new DateOnly(hoy.Year, hoy.Month, 1);
        var puntos = Enumerable.Range(0, meses)
            .Select(i => ultimo.AddMonths(-(meses - 1 - i)))
            .Select((d, i) => new PuntoSerieCuadroDto(d.Year, d.Month, EstadoCifraMensual.Estimado, cifras(i), facturacion))
            .ToList();
        return new SerieCuadroDireccionDto(
            puntos,
            [new SerieTenantCuadroDto(TenantA, "Refrielectric S.A.", puntos), new SerieTenantCuadroDto(TenantB, "Montajes Ebro S.L.", puntos)],
            0);
    }

    private static LineaFacturacionCuadroDto Linea(ConceptoTarifaOperadorCae concepto, string nombre, int unidades, decimal importe, string moneda = "EUR") =>
        new(concepto, nombre, moneda, unidades, importe);

    private static IReadOnlyList<LineaFacturacionCuadroDto> LineasDeTarifa() =>
        [Linea(ConceptoTarifaOperadorCae.ObraNueva, "Obra nueva", 100, 150m), Linea(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, "Visita de Trabajador extranjero", 300, 600m)];

    private IRenderedComponent<CuadroDireccionPagina> PintarConSerie(
        CuadroDireccionDto cuadro, IReadOnlyList<LineaFacturacionCuadroDto>? lineas, string rol = Roles.DireccionCae) =>
        Pintar(cuadro, rol, out _, serie: _ => SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 30), lineas));

    // ---------------------------------------------------------------- Gestiones del mes

    [Fact]
    public void La_fila_de_KPI_son_seis_tarjetas_y_las_gestiones_no_son_una_de_ellas()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);

        var kpis = cut.FindAll(".cuadro-kpi").Select(k => k.GetAttribute("data-kpi")).ToList();
        kpis.Should().Equal("facturacion", "extranjeros", "trabajadores", "centros", "revisar", "cumplimiento");
        cut.FindAll("[data-kpi=gestiones], [data-kpi=sintarifa]").Should().BeEmpty(
            "«Gestiones del mes» vive en «Facturación y gestiones por mes» y los trabajadores sin tarifa en la pestaña Facturación");
    }

    [Fact]
    public void El_KPI_de_contrato_y_cuota_sale_con_el_estado_vacio_de_la_maqueta()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);

        var tarjeta = cut.Find("[data-kpi=revisar]");
        tarjeta.GetAttribute("data-sin-modelo").Should().Be("contrato");
        tarjeta.QuerySelector(".cuadro-kpi-valor")!.TextContent.Trim().Should().Be("—");
    }

    // ---------------------------------------------------------------- Visión general: facturación, serie y alertas

    [Fact]
    public void El_KPI_de_facturacion_valora_con_las_tarifas_y_compara_con_el_mes_anterior()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());

        cut.Find("[data-kpi=facturacion] .cuadro-kpi-valor").TextContent.Should().Contain("750").And.Contain("€");
        cut.Find("[data-kpi=facturacion] .cuadro-kpi-sub").TextContent.Should().Contain("+0,0 %");
    }

    [Fact]
    public void Sin_tarifas_la_facturacion_no_inventa_importes()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), []);

        cut.Find("[data-kpi=facturacion] .cuadro-kpi-valor").TextContent.Trim().Should().Be("—");
    }

    [Fact]
    public void La_serie_de_facturacion_y_gestiones_se_pinta_con_barras_y_linea()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());

        var grafico = cut.Find("[data-grafico=evolucion]");
        grafico.QuerySelectorAll("rect.gs-barra").Length.Should().BeGreaterThan(0);
        grafico.QuerySelectorAll("polyline.gs-linea").Length.Should().Be(1);
        grafico.QuerySelectorAll(".gs-zona").Length.Should().Be(6);
    }

    [Fact]
    public void Las_etiquetas_de_datos_pintan_el_valor_de_cada_barra()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());
        cut.Find("[data-grafico=evolucion]").QuerySelectorAll(".gs-valor").Length.Should().Be(0);

        cut.Find("#cuadro-etiquetas").Click();

        cut.Find("#cuadro-etiquetas").GetAttribute("aria-checked").Should().Be("true");
        cut.Find("[data-grafico=evolucion]").QuerySelectorAll(".gs-valor").Length.Should().Be(6);
    }

    [Fact]
    public void El_grafico_de_la_serie_lleva_eje_Y_con_rejilla_y_las_etiquetas_en_texto_HTML()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());

        var grafico = cut.Find("[data-grafico=evolucion]");
        grafico.QuerySelectorAll(".gs-marca-y").Length.Should().BeGreaterThanOrEqualTo(4, "el eje Y rotula cuatro o cinco divisiones");
        grafico.QuerySelectorAll("line.gs-rejilla").Length.Should().BeGreaterThanOrEqualTo(4);
        grafico.QuerySelectorAll("svg text").Length.Should().Be(0, "el texto va en HTML para que no se escale con el SVG");
    }

    /// <summary>
    /// Pruebas de flujos del 2026-10-21 (Tarnaval: 83 % en el Cuadro, 25 % en /empresas): la alerta y la columna miden el
    /// contexto Empresa (pares exigidos, los que faltan en contra), no los documentos de Trabajador guardados de Inicio.
    /// Aquí los documentos guardados están al 90 % y los pares exigidos al 62 %: avisa por el 62.
    /// </summary>
    [Fact]
    public void Una_Empresa_con_cumplimiento_bajo_sale_en_Que_requiere_una_decision()
    {
        var cuadro = Cuadro(EstadoCifraMensual.Parcial);
        var baja = cuadro.Tenants[1] with { Cifras = cuadro.Tenants[1].Cifras with { Cumplimiento = new CumplimientoActualDto(9, 0, 0, 1, 0, 0, 0, 90, false, DateTime.UtcNow, Exigido: new(62, 100)) } };
        cuadro = cuadro with { Tenants = [cuadro.Tenants[0], baja] };

        var cut = Pintar(cuadro, Roles.DireccionCae, out _);

        var alerta = cut.Find(".cuadro-alerta");
        alerta.TextContent.Should().Contain("Montajes Ebro S.L.").And.Contain("62 %");
        alerta.GetAttribute("data-prioridad").Should().Be("1");
    }

    private static SerieCuadroDireccionDto SoloElUltimoMes(SerieCuadroDireccionDto serie)
    {
        CifrasCuadroDireccionDto Vacias(CifrasCuadroDireccionDto c) => c with
        {
            TrabajadoresActivos = c.TrabajadoresActivos with { Valor = 0 },
            CentrosActivos = c.CentrosActivos with { Valor = 0 },
            DocumentosGestionados = c.DocumentosGestionados with { Valor = 0 },
            Gestiones = new CifraMensual(0, EstadoCifraMensual.Estimado),
        };
        var puntos = serie.Total.Select((p, i) => i == serie.Total.Count - 1 ? p : p with { Cifras = Vacias(p.Cifras), Facturacion = null }).ToList();
        return serie with { Total = puntos };
    }

    [Fact]
    public void El_grafico_con_barras_y_linea_lleva_la_leyenda_arriba_y_el_eje_de_la_linea_a_la_derecha()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());

        var grafico = cut.Find("[data-grafico=evolucion]");
        grafico.QuerySelectorAll(".gs-eje-derecho .gs-marca-y").Length.Should().BeGreaterThanOrEqualTo(4, "el eje derecho rotula las gestiones");
        grafico.InnerHtml.IndexOf("gs-leyenda", StringComparison.Ordinal).Should().BeLessThan(
            grafico.InnerHtml.IndexOf("gs-cuerpo", StringComparison.Ordinal), "la leyenda va encima del gráfico");
    }

    [Fact]
    public void Con_mas_de_seis_meses_el_eje_X_rotula_uno_de_cada_dos_con_el_rotulo_corto()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _,
            serie: _ => SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 30), LineasDeTarifa(), 12));

        var etiquetas = cut.Find("[data-grafico=evolucion]").QuerySelectorAll(".gs-etiqueta").Select(e => e.TextContent.Trim()).ToList();

        etiquetas.Should().HaveCount(6);
        etiquetas.Should().OnlyContain(e => System.Text.RegularExpressions.Regex.IsMatch(e, @"^[a-z]{3}( \d{2})?$"), "el rótulo es «oct» y solo enero lleva el año («ene 24»)");
    }

    [Fact]
    public void Un_minigrafico_con_un_solo_mes_con_actividad_dice_que_no_hay_tendencia_en_vez_de_pintar_una_raya()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _,
            serie: _ => SoloElUltimoMes(SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 30), LineasDeTarifa())));

        cut.Find("[data-kpi=trabajadores] .gs-sin-tendencia").TextContent.Trim().Should().Be("no hay tendencia; aparece desde el segundo mes");
        cut.FindAll("[data-kpi=trabajadores] polyline").Should().BeEmpty();
    }

    [Fact]
    public void El_subtexto_de_la_facturacion_del_primer_mes_no_dice_sin_mes_anterior_vs_mes_anterior()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _,
            serie: _ => SoloElUltimoMes(SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 30), LineasDeTarifa())));

        var sub = cut.Find("[data-kpi=facturacion] .cuadro-kpi-sub").TextContent;

        sub.Trim().Should().Be("tarifas de hoy");
        sub.Should().NotContain("sin mes anterior");
    }

    [Fact]
    public void La_Cartera_trae_las_columnas_de_la_maqueta_con_guion_donde_no_hay_dato()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());

        IrA(cut, "Cartera por Gestor CAE");

        cut.FindAll("[data-tabla=cartera] th").Select(t => t.TextContent.Trim()).Should().Equal(
            "Gestor CAE", "Equipo", "Empresas", "Emp. tit.", "Centros", "Trabajadores", "Extranjeros", "Gestiones", "Mayor Empresa", "Índice");
        var fila = cut.Find("[data-tabla=cartera] tr[data-gestor]:not([data-gestor=sin])").QuerySelectorAll("td").Select(c => c.TextContent.Trim()).ToList();
        fila[0].Should().Be("Marta Gestora");
        fila[1].Should().Be("—", "no hay modelo de equipos");
        fila[2].Should().Be("1");
        fila[3].Should().Be("—", "no hay modelo de contrato");
        fila[4].Should().Be("30");
        fila[5].Should().Be("300");
        fila[6].Should().Be("7", "Trabajadores con documento de extranjero, no visitas facturables");
        fila[7].Should().Be("42");
        fila[8].Should().Contain("100");
        fila[9].Should().Be("100", "una sola cartera es la media");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Exportar a Excel");
        cut.Markup.Should().Contain("Qué mide y qué no mide");
    }

    [Fact]
    public void Exportar_a_Excel_va_en_la_fila_de_cabecera_de_la_tarjeta_a_la_derecha_del_titulo()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());

        IrA(cut, "Cartera por Gestor CAE");

        var exportar = cut.Find("[data-tarjeta=c-tabla] .tarjeta-header .tarjeta-acciones button.cuadro-exportar");
        exportar.TextContent.Trim().Should().Be("Exportar a Excel");
        cut.FindAll("[data-tarjeta=c-tabla] .tarjeta-contenido .cuadro-exportar").Should().BeEmpty("el botón ya no cuelga del contenido de la tarjeta");
    }

    [Fact]
    public void Las_tarjetas_cuelgan_de_un_envoltorio_del_propio_Cuadro_para_que_el_CSS_aislado_llegue_a_Tarjeta()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());

        IrA(cut, "Cartera por Gestor CAE");

        var tarjeta = cut.Find("[data-tarjeta=c-tabla]");
        tarjeta.ClassList.Should().Contain("cuadro-bloque");
        tarjeta.QuerySelector(".tarjeta-titulo")!.ParentElement!.ClassList.Should().Contain("tarjeta-header");
        tarjeta.Attributes.Select(a => a.Name).Should().Contain(n => n.StartsWith("b-", StringComparison.Ordinal), "el envoltorio lleva el atributo de ámbito de CuadroDireccion");
    }

    [Fact]
    public void Los_KPI_van_en_su_propia_fila_y_las_tarjetas_anchas_fuera_de_ella()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());

        cut.FindAll(".cuadro-kpis > .cuadro-kpi").Should().HaveCount(6);
        cut.FindAll(".cuadro-kpis [data-tarjeta]").Should().BeEmpty("en móvil la fila de KPI se desplaza y no debe arrastrar a las tarjetas");
        cut.FindAll(".cuadro-rejilla > [data-tarjeta]").Should().NotBeEmpty();
    }

    [Fact]
    public void La_Demanda_pone_los_meses_de_mas_y_menos_carga_junto_a_la_recomendacion_y_despues_las_dos_tarjetas_anchas()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());

        IrA(cut, "Demanda");

        cut.FindAll("[data-tarjeta]").Select(t => t.GetAttribute("data-tarjeta")).Should().Equal("p-franja", "p-estacional", "p-heatmap", "p-calendario");
        cut.Find("[data-tarjeta=p-franja]").ClassList.Should().NotContain("cuadro-ancha");
        cut.Find("[data-tarjeta=p-estacional]").ClassList.Should().NotContain("cuadro-ancha");
        cut.Find("[data-tarjeta=p-heatmap]").ClassList.Should().Contain("cuadro-ancha");
        cut.Find("[data-tarjeta=p-calendario]").ClassList.Should().Contain("cuadro-ancha");
    }

    [Fact]
    public void Sin_medida_de_extranjeros_en_alguna_Empresa_de_la_cartera_la_celda_sale_con_guion()
    {
        var cuadro = Cuadro(EstadoCifraMensual.Parcial);
        var sinMedida = cuadro.Tenants[0] with { Cifras = cuadro.Tenants[0].Cifras with { Extranjeros = null } };
        var cut = Pintar(cuadro with { Tenants = [sinMedida, cuadro.Tenants[1]] }, Roles.DireccionCae, out _);

        IrA(cut, "Cartera por Gestor CAE");

        cut.Find("[data-tabla=cartera] tr[data-gestor]:not([data-gestor=sin])").QuerySelectorAll("td")[6].TextContent.Trim().Should().Be("—");
    }

    [Fact]
    public void La_comparativa_anual_cuenta_gestiones_de_extranjeros_y_no_Trabajadores_extranjeros()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _,
            serie: q => q.Meses == 24 ? DosAnyos(true) : SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 25)));

        IrA(cut, "Comparativa anual");

        cut.WaitForAssertion(() =>
        {
            var tabla = cut.Find("[data-tabla=comparativa]");
            tabla.QuerySelectorAll("th").Select(h => h.TextContent.Trim()).Should().Contain("Gestiones de extranjeros");
            tabla.QuerySelector("tbody tr")!.QuerySelectorAll("td")[5].TextContent.Trim().Should().Be("—", "sin tarifa de visita de Trabajador extranjero no hay gestiones que contar; los 7 Trabajadores extranjeros solo van en la Cartera");
        });
    }
    [Fact]
    public void El_indice_de_cartera_pinta_una_barra_por_Gestor_CAE_con_la_media_marcada()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());

        IrA(cut, "Cartera por Gestor CAE");

        var indice = cut.Find("[data-grafico=indice]");
        indice.QuerySelectorAll(".cuadro-indice-fila").Length.Should().Be(1, "solo el Gestor CAE con cartera principal; las Empresas sin principal no entran");
        indice.QuerySelector(".cuadro-indice-barra")!.GetAttribute("data-nivel").Should().Be("medio");
        indice.TextContent.Should().Contain("media = 100");
    }



    // ---------------------------------------------------------------- Facturación





    // ---------------------------------------------------------------- Empresas

    [Fact]
    public void La_tabla_de_Empresas_trae_las_columnas_de_la_maqueta_y_vacias_las_que_no_tienen_modelo()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());

        IrA(cut, "Empresas");

        var cabeceras = cut.FindAll("[data-tabla=empresas] th").Select(t => t.TextContent.Trim()).ToList();
        cabeceras.Should().Equal("Empresa", "Gestor CAE", "Trab.", "Centros", "Emp. tit.", "Extranj.", "Rotación", "Factura mes", "€ / gestión", "Cumpl.");
        var fila = cut.Find($"tr[data-empresa='{TenantA}']");
        fila.QuerySelector("td[data-sin-modelo=contrato]")!.TextContent.Trim().Should().Be("—");
        fila.TextContent.Should().Contain("Marta Gestora");
        cut.Markup.Should().NotContain("Responde", "la maqueta no lleva la columna ni la nota «Responde»");
        fila.QuerySelectorAll("td.cuadro-delta")[0].TextContent.Trim().Should().Be("300 = /300", "valor, diferencia con el mes anterior y ese mes anterior");
    }

    [Fact]
    public void Pulsar_una_cabecera_ordena_la_tabla_de_Empresas_y_marca_aria_sort()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());
        IrA(cut, "Empresas");

        cut.FindAll("[data-tabla=empresas] th button").Single(b => b.TextContent.Trim() == "Empresa").Click();

        var orden = cut.FindAll("[data-tabla=empresas] tbody tr[data-empresa]").Select(f => f.GetAttribute("data-empresa")).ToList();
        orden.Should().Equal(TenantB.ToString(), TenantA.ToString());
        cut.FindAll("[data-tabla=empresas] th").First().GetAttribute("aria-sort").Should().Be("ascending");
    }

    [Fact]
    public void Una_fila_de_Empresa_se_expande_con_su_detalle_y_su_minigrafico()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());
        IrA(cut, "Empresas");
        cut.FindAll($"[data-detalle='{TenantA}']").Should().BeEmpty();

        cut.Find($"tr[data-empresa='{TenantA}'] .cuadro-expandir").Click();

        var detalle = cut.Find($"[data-detalle='{TenantA}']");
        detalle.TextContent.Should().Contain("Altas trab.").And.Contain("Documentos");
        detalle.QuerySelectorAll("polyline.gs-linea").Length.Should().Be(1);
    }

    [Fact]
    public void Las_tarjetas_de_Empresas_pintan_la_dispersion_la_concentracion_y_el_contrato_vacio()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());

        IrA(cut, "Empresas");

        cut.FindAll("[data-grafico=rendimiento] .gd-punto").Count.Should().Be(2);
        cut.Find("[data-grafico=pareto]").QuerySelectorAll("polyline.gs-linea").Length.Should().Be(1);
        cut.Find("[data-tarjeta=cl-contratos] [data-sin-modelo=contrato]").TextContent.Should().Contain("Todavía no hay contrato");
    }

    [Fact]
    public void La_dispersion_lleva_ejes_con_rotulos_la_mediana_y_la_leyenda_de_la_maqueta()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());
        IrA(cut, "Empresas");

        var grafico = cut.Find("[data-grafico=rendimiento]");

        grafico.QuerySelectorAll(".gd-eje-y .gd-marca-y").Length.Should().BeGreaterThanOrEqualTo(4);
        grafico.QuerySelectorAll(".gd-eje-x .gd-marca-x").Length.Should().BeGreaterThanOrEqualTo(4);
        grafico.QuerySelector(".gd-mediana-rotulo")!.TextContent.Should().StartWith("mediana ").And.Contain("€");
        grafico.QuerySelectorAll(".gd-leyenda span").Select(s => s.TextContent.Trim()).Should().Equal("Resto de Empresas", "Por debajo del 80 % de la mediana");
        grafico.QuerySelector(".gd-titulo-x")!.TextContent.Should().Be("Gestiones del mes");
        grafico.QuerySelectorAll("svg text").Length.Should().Be(0, "el texto va en HTML");
    }

    [Fact]
    public void La_concentracion_lleva_leyenda_de_la_maqueta_eje_doble_y_la_marca_del_ochenta_por_ciento()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());
        IrA(cut, "Empresas");

        var grafico = cut.Find("[data-grafico=pareto]");

        grafico.QuerySelectorAll(".gs-leyenda span").Select(s => s.TextContent.Trim()).Should().Equal("Primer 50 %", "Resto", "Acumulado");
        grafico.QuerySelectorAll(".gs-eje-derecho .gs-marca-y").Select(s => s.TextContent.Trim()).Should().Equal("0 %", "50 %", "80 %", "100 %");
        grafico.QuerySelectorAll("line.gs-referencia").Length.Should().Be(1);
        grafico.QuerySelectorAll(".gs-etiqueta").Select(e => e.TextContent.Trim()).Should().Equal("1", "2");
        grafico.QuerySelectorAll(".gs-tip").First().TextContent.Should().StartWith("1. ");
    }

    // ---------------------------------------------------------------- Demanda

    [Fact]
    public void El_mapa_de_calor_suma_los_seis_meses_de_la_serie_y_se_pinta_con_veinticuatro()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial, solicitudes: 4), Roles.DireccionCae, out _,
            serie: _ => SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 4)));

        IrA(cut, "Demanda");

        cut.FindAll("[data-suprimida=mapa-calor]").Should().BeEmpty("seis meses de cuatro solicitudes suman veinticuatro");
        cut.FindAll(".cuadro-calor-celda").Should().NotBeEmpty();
    }

    [Fact]
    public void El_mapa_de_calor_se_suprime_si_los_seis_meses_suman_menos_de_veinte()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial, solicitudes: 120), Roles.DireccionCae, out _,
            serie: _ => SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 3)));

        IrA(cut, "Demanda");

        cut.Find("[data-suprimida=mapa-calor]").TextContent.Should().Be("No se muestra: menos de 20 conversaciones");
    }

    [Fact]
    public void El_mapa_de_calor_no_enseña_el_recuento_de_una_celda_con_menos_de_cinco_solicitudes()
    {
        var muchas = new[] { new FranjaSolicitudesDto(DayOfWeek.Monday, 9, 40) };
        var pocas = new[] { new FranjaSolicitudesDto(DayOfWeek.Monday, 9, 40), new FranjaSolicitudesDto(DayOfWeek.Wednesday, 11, 3) };
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _,
            serie: _ => SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 25, i == 0 ? pocas : muchas)));

        IrA(cut, "Demanda");

        var titulos = cut.FindAll(".cuadro-calor-celda").Select(c => c.GetAttribute("title")!).ToList();
        titulos.Should().ContainSingle(t => t.Contains("menos de 5 solicitudes"));
        titulos.Should().NotContain(t => t.Contains(" 3 solicitudes"), "la cifra real de una celda pequeña no sale ni en el tooltip");
        titulos.Should().Contain(t => t.Contains("240 solicitudes"));
        cut.FindAll(".cuadro-calor-celda .cuadro-solo-lector").Select(s => s.TextContent).Should().Contain("<5");
    }

    [Fact]
    public void Las_barras_de_estados_y_canales_ponen_menor_que_cinco_en_vez_de_un_recuento_pequeño()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _, comunicaciones: Conversaciones(40, 30, 3));

        IrA(cut, "Comunicaciones");

        static string Compacto(string texto) => System.Text.RegularExpressions.Regex.Replace(texto, "\\s+", " ");
        var canal = Compacto(cut.Find("[data-barras=canal]").TextContent);
        canal.Should().Contain("WhatsApp <5").And.Contain("Correo 30");
        Compacto(cut.Find("[data-barras=estados]").TextContent).Should().Contain("En espera de tercero <5").And.Contain("Abierta 40");
    }

    [Fact]
    public void La_franja_de_refuerzo_dice_las_horas_y_los_dias_de_mas_peso()
    {
        var franjas = new[]
        {
            new FranjaSolicitudesDto(DayOfWeek.Monday, 9, 40), new FranjaSolicitudesDto(DayOfWeek.Monday, 10, 40),
            new FranjaSolicitudesDto(DayOfWeek.Tuesday, 10, 30), new FranjaSolicitudesDto(DayOfWeek.Friday, 15, 5),
        };
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _,
            serie: _ => SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 25, franjas)));

        IrA(cut, "Demanda");

        cut.Find("[data-franja=texto]").TextContent.Should().Contain("9–13 h").And.Contain("Lun");
    }

    [Fact]
    public void El_calendario_de_gestiones_por_dia_pinta_un_cuadro_por_dia_laborable()
    {
        var hoy = DiaDeNegocio.Hoy();
        // El calendario solo pinta de lunes a viernes, y el día 1 puede caer en fin de semana.
        var diaLaborable = new DateOnly(hoy.Year, hoy.Month, 1);
        while (diaLaborable.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            diaLaborable = diaLaborable.AddDays(1);
        }
        var dias = new GestionesPorDiaCuadroDireccionDto(
            [new GestionesPorDiaTenantDto(TenantA, "Refrielectric S.A.", [new GestionesDelDiaDto(diaLaborable, 5)])], 0);
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _,
            serie: _ => SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 25)), gestionesDia: dias);

        IrA(cut, "Demanda");

        cut.FindAll(".cuadro-calendario-dia").Count.Should().BeGreaterThan(20);
        cut.FindAll(".cuadro-calendario-dia[title*='5 gestiones']").Should().HaveCount(1);
    }

    [Fact]
    public void La_estacionalidad_pide_los_veinticuatro_meses_al_abrir_la_pestaña()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out var mediador,
            serie: q => SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 25), null, q.Meses));

        mediador.SeriesPedidas.Should().ContainSingle(q => q.Meses == 36 && q.ConFacturacion);

        IrA(cut, "Demanda");

        cut.WaitForAssertion(() => mediador.SeriesPedidas.Should().Contain(q => q.Meses == 24 && !q.ConFacturacion));
    }

    // ---------------------------------------------------------------- Comunicaciones

    private static ComunicacionesCuadroDireccionDto Conversaciones(int abiertas, int correo, int whatsapp) =>
        ObtenerComunicacionesCuadroDireccionQueryHandler.Fusionar(2026, 10,
        [
            new ComunicacionesTenantDto(TenantA, "Refrielectric S.A.",
                [new ConteoPorEstadoDto(EstadoConversacion.Abierta, abiertas), new ConteoPorEstadoDto(EstadoConversacion.EnEsperaDeTercero, 3)],
                [new ConteoPorCanalDto(CanalConversacion.Correo, correo), new ConteoPorCanalDto(CanalConversacion.WhatsApp, whatsapp)]),
        ], 0);

    [Fact]
    public void Comunicaciones_enseña_estados_y_canales_con_modelo_y_estado_vacio_sin_el()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _, comunicaciones: Conversaciones(40, 30, 12));

        IrA(cut, "Comunicaciones");

        var estados = cut.Find("[data-barras=estados]");
        estados.TextContent.Should().Contain("Abierta").And.Contain("40").And.Contain("En espera de tercero").And.Contain("Esperando respuesta externa");
        estados.QuerySelector("[data-sin-modelo=sla]")!.TextContent.Should().Contain("Fuera de SLA").And.Contain("—");
        var canal = cut.Find("[data-barras=canal]");
        canal.TextContent.Should().Contain("Correo").And.Contain("30").And.Contain("WhatsApp").And.Contain("12").And.Contain("Llamada");
        cut.Find("[data-tarjeta=cm-kpis] [data-sin-modelo=tiempos]").TextContent.Should().Contain("medidas de tiempo");
        cut.Find("[data-tarjeta=cm-intencion] [data-sin-modelo=intencion]").TextContent.Should().Contain("intención");
        cut.Find("[data-tarjeta=cm-equipos]").TextContent.Should().Contain("Todavía no hay medidas de tiempo por equipo");
    }

    [Fact]
    public void Comunicaciones_suprime_estados_y_canales_bajo_veinte_conversaciones()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _, comunicaciones: Conversaciones(10, 8, 5));

        IrA(cut, "Comunicaciones");

        cut.Find("[data-suprimida=estados]").TextContent.Should().Be("No se muestra: menos de 20 conversaciones");
        cut.Find("[data-suprimida=canal]").TextContent.Should().Be("No se muestra: menos de 20 conversaciones");
        cut.FindAll("[data-barras]").Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Vacaciones

    [Fact]
    public void Vacaciones_y_cobertura_sale_con_sus_dos_tarjetas_agregadas_en_estado_vacio()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);

        IrA(cut, "Vacaciones y cobertura");

        cut.FindAll("[data-sin-modelo=vacaciones]").Count.Should().Be(3, "cobertura, saldo y «Vacaciones del año por Gestor CAE», las tres sin modelo de vacaciones");
        cut.Markup.Should().Contain("Capacidad del equipo ausente por vacaciones").And.Contain("Saldo de vacaciones").And.Contain("Vacaciones del año por Gestor CAE");
        cut.Markup.Should().NotContain("Ocupación de Gestores CAE");
    }

    // ---------------------------------------------------------------- Comparativa anual

    private static SerieCuadroDireccionDto DosAnyos(bool conDatosElAnyoPrevio)
    {
        var hoy = DiaDeNegocio.Hoy();
        var ultimo = new DateOnly(hoy.Year, hoy.Month, 1);
        var puntos = Enumerable.Range(0, 24)
            .Select(i => ultimo.AddMonths(-(23 - i)))
            .Select(d => new PuntoSerieCuadroDto(
                d.Year, d.Month, EstadoCifraMensual.Estimado,
                Cifras(EstadoCifraMensual.Estimado, 25) with
                {
                    Gestiones = new CifraMensual(d.Year == hoy.Year ? 50 : conDatosElAnyoPrevio ? 40 : 0, EstadoCifraMensual.Estimado),
                },
                [Linea(ConceptoTarifaOperadorCae.ObraNueva, "Obra nueva", 10, d.Year == hoy.Year ? 200m : 100m)]))
            .ToList();
        return new SerieCuadroDireccionDto(puntos, [new SerieTenantCuadroDto(TenantA, "Refrielectric S.A.", puntos)], 0);
    }

    [Fact]
    public void La_comparativa_anual_pide_veinticuatro_meses_con_facturacion_y_pinta_una_linea_por_año()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out var mediador,
            serie: q => q.Meses == 24 ? DosAnyos(true) : SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 25)));

        IrA(cut, "Comparativa anual");

        cut.WaitForAssertion(() => mediador.SeriesPedidas.Should().Contain(q => q.Meses == 24 && q.ConFacturacion));
        cut.WaitForAssertion(() => cut.Find("[data-grafico=comparativa]").QuerySelectorAll("polyline.gs-linea").Length.Should().BeGreaterThanOrEqualTo(2));
        var tabla = cut.Find("[data-tabla=comparativa]");
        tabla.QuerySelectorAll("tbody tr").Length.Should().Be(2);
        tabla.QuerySelector("tbody tr td[data-sin-modelo=empresas]")!.TextContent.Trim().Should().Be("—");
    }

    [Fact]
    public void El_selector_de_metrica_de_la_comparativa_ofrece_Extranjeros_y_no_Documentos_gestionados()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _,
            serie: q => q.Meses == 24 ? DosAnyos(true) : SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 25)));

        IrA(cut, "Comparativa anual");

        cut.WaitForAssertion(() => cut.FindAll(".cuadro-seg button").Should().NotBeEmpty());
        var botones = cut.FindAll(".cuadro-seg button");
        botones.Select(b => b.TextContent.Trim()).Should().Contain("Extranjeros").And.NotContain("Documentos gestionados");
        botones.First(b => b.TextContent.Trim() == "Extranjeros").Click();
        cut.FindAll(".cuadro-seg button").First(b => b.TextContent.Trim() == "Extranjeros").GetAttribute("aria-pressed").Should().Be("true");
    }

    [Fact]
    public void Sin_un_mismo_mes_en_dos_años_la_comparativa_dice_que_aparece_a_partir_del_mes_trece()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _,
            serie: q => q.Meses == 24 ? DosAnyos(false) : SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 25)));

        IrA(cut, "Comparativa anual");

        cut.WaitForAssertion(() => cut.Find("[data-tarjeta=cmp-linea] [data-sin-modelo=historico]").TextContent.Should().Contain("trece meses"));
    }

    [Fact]
    public void Con_menos_de_trece_meses_de_operacion_la_comparativa_solo_dice_que_falta_historia_sin_fila_de_ceros()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _,
            serie: q => q.Meses == 24 ? SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 25), LineasDeTarifa(), 8) : SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 25)));

        IrA(cut, "Comparativa anual");

        cut.WaitForAssertion(() => cut.Find("[data-tarjeta=cmp-linea] [data-sin-modelo=historico]").TextContent.Should().Contain("trece meses"));
        cut.Find("[data-tarjeta=cmp-tabla] [data-sin-modelo=historico]").Should().NotBeNull();
        cut.FindAll("[data-tabla=comparativa]").Should().BeEmpty("sin un mes igual de otro año no hay fila que comparar");
        cut.FindAll("[data-grafico=comparativa]").Should().BeEmpty();
    }
    // ---------------------------------------------------------------- preferencias

    private BunitJSModuleInterop ModuloPreferencias(string? guardado = null)
    {
        var modulo = JSInterop.SetupModule("./js/cuadro-direccion.js");
        modulo.Mode = JSRuntimeMode.Loose;
        modulo.Setup<string?>("leer", _ => true).SetResult(guardado);
        return modulo;
    }

    [Fact]
    public void Ocultar_una_tarjeta_la_quita_de_la_pestaña_y_se_recuerda_en_el_navegador()
    {
        var modulo = ModuloPreferencias();
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);
        cut.FindAll("[data-kpi=extranjeros]").Should().HaveCount(1);

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Personalizar esta pestaña").Click();
        cut.Find("[data-tarjeta-lista=k-ext] input[type=checkbox]").Change(false);

        cut.FindAll("[data-kpi=extranjeros]").Should().BeEmpty();
        var guardado = modulo.Invocations["guardar"].Last().Arguments[1]!.ToString()!;
        guardado.Should().Contain("k-ext");
        modulo.Invocations["guardar"].Last().Arguments[0].Should().Be($"cuadro-direccion:preferencias:{UsuarioPrueba}",
            "la clave lleva el usuario: dos personas en el mismo navegador no comparten preferencias");
        modulo.Invocations["leer"].Single().Arguments[0].Should().Be($"cuadro-direccion:preferencias:{UsuarioPrueba}");
    }

    [Theory]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.CoordinadorCae)]
    public void Una_pestaña_sin_tarjetas_ofrece_recuperarlas_sin_escribir_datos_de_negocio(string rol)
    {
        ModuloPreferencias();
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), rol, out var mediador);
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Personalizar esta pestaña").Click();
        var tarjetas = cut.FindAll("[data-tarjeta-lista]").Select(e => e.GetAttribute("data-tarjeta-lista")).ToList();
        foreach (var tarjeta in tarjetas)
            cut.Find($"[data-tarjeta-lista='{tarjeta}'] input[type=checkbox]").Change(false);

        cut.Find("[data-vacio=personalizacion]").TextContent.Should().Be(
            "Esta pestaña está vacía. Usa «Personalizar esta pestaña» para elegir qué ver.");
        cut.FindAll(".cuadro-kpi, .cuadro-bloque").Should().BeEmpty();
        mediador.Reaperturas.Should().BeEmpty();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Restablecer esta pestaña").Click();
        cut.FindAll("[data-vacio=personalizacion]").Should().BeEmpty();
        cut.FindAll(".cuadro-kpi").Should().HaveCount(6);
    }

    [Theory]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.CoordinadorCae)]
    public void Personalizar_identifica_la_pestaña_y_expone_su_estado_sin_enviar_comandos(string rol)
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), rol, out var mediador);
        var boton = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Personalizar esta pestaña");
        boton.GetAttribute("aria-pressed").Should().Be("false");
        cut.Find("h1").TextContent.Should().Be("Cuadro de dirección");
        var pestanas = cut.FindAll("button[role=tab]");
        var etiquetas = new[] { "Visión general", "Facturación", "Empresas", "Cartera por Gestor CAE", "Demanda", "Vacaciones y cobertura", "Comunicaciones", "Comparativa anual" };
        pestanas.Should().HaveCount(etiquetas.Length);
        for (var i = 0; i < etiquetas.Length; i++)
            pestanas[i].TextContent.Trim().Should().StartWith(etiquetas[i]);
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Presentar");
        cut.Find("button[role=switch]").TextContent.Should().Contain("Etiquetas de datos");

        boton.Click();

        cut.Find("#cuadro-pers-titulo").TextContent.Should().Be("Personalizar: Visión general");
        cut.Find("button[aria-controls=cuadro-panel-personalizar]").GetAttribute("aria-pressed").Should().Be("true");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Restablecer esta pestaña");
        mediador.Reaperturas.Should().BeEmpty();

        IrA(cut, "Demanda");
        cut.FindAll("[data-panel=personalizar]").Should().BeEmpty();
        cut.Find("button[aria-controls=cuadro-panel-personalizar]").Click();
        cut.Find("#cuadro-pers-titulo").TextContent.Should().Be("Personalizar: Demanda");
    }

    [Theory]
    [InlineData(Roles.DireccionCae, true)]
    [InlineData(Roles.CoordinadorCae, true)]
    [InlineData(Roles.Consulta, false)]
    [InlineData(Roles.Administrador, true)]
    [InlineData(Roles.GestorCae, false)]
    public void La_ruta_direccion_admite_por_rol_a_Direccion_Coordinacion_y_Administrador(string rol, bool permitido)
    {
        // La ruta va por política: el rol de equipo, o una Sesión Privilegiada
        // con acceso total al Tenant (Actor de Plataforma TALVEG auditado).
        var autorizacion = typeof(CuadroDireccionPagina)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single();

        autorizacion.Policy.Should().Be(CaeManager.Web.Services.Autorizacion.PoliticaCuadroDireccion.Nombre);
        CaeManager.Web.Services.Autorizacion.PoliticaCuadroDireccion.Roles.Contains(rol).Should().Be(permitido);
    }

    [Theory]
    [InlineData(Roles.DireccionCae, true)]
    [InlineData(Roles.CoordinadorCae, false)]
    [InlineData(Roles.Administrador, false)]
    // Sesión Privilegiada: el middleware quita los claims de rol del Actor de Plataforma.
    [InlineData("", false)]
    public void Reabrir_un_mes_cerrado_sigue_siendo_solo_de_Direccion(string rol, bool visible)
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), rol, out _);

        cut.FindAll("button").Any(b => b.TextContent.Trim() == "Reabrir un mes cerrado").Should().Be(visible);
    }

    [Fact]
    public void Sin_modulo_de_navegador_exportar_avisa_en_vez_de_no_hacer_nada()
    {
        var modulo = JSInterop.SetupModule("./js/cuadro-direccion.js");
        modulo.Mode = JSRuntimeMode.Loose;
        modulo.Setup<string?>("leer", _ => true).SetResult(null);
        modulo.SetupVoid("descargar", _ => true).SetException(new Microsoft.JSInterop.JSException("bloqueado"));
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);
        IrA(cut, "Empresas");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Exportar a Excel").Click();

        cut.Find("[data-aviso=exportar]").TextContent.Should().Contain("No se pudo exportar");
    }

    [Fact]
    public void Las_flechas_reordenan_las_tarjetas_de_la_pestaña()
    {
        ModuloPreferencias();
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Personalizar esta pestaña").Click();
        var antes = cut.FindAll("[data-tarjeta-lista]").Select(l => l.GetAttribute("data-tarjeta-lista")).ToList();
        antes.Take(2).Should().Equal("k-fact", "k-ext");

        cut.Find("[data-tarjeta-lista=k-ext] button[aria-label^='Subir']").Click();

        cut.FindAll("[data-tarjeta-lista]").Select(l => l.GetAttribute("data-tarjeta-lista")).Take(2).Should().Equal("k-ext", "k-fact");
    }

    [Fact]
    public void Las_flechas_de_los_extremos_y_las_de_una_tarjeta_oculta_estan_desactivadas_como_en_la_maqueta()
    {
        ModuloPreferencias("{\"Ocultas\":[\"k-ext\"],\"Orden\":{},\"Etiquetas\":false,\"AvisosVistos\":[]}");
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Personalizar esta pestaña").Click();
        var ids = cut.FindAll("[data-tarjeta-lista]").Select(l => l.GetAttribute("data-tarjeta-lista")!).ToList();

        cut.Find($"[data-tarjeta-lista={ids[0]}] button[aria-label^='Subir']").HasAttribute("disabled").Should().BeTrue("la primera no sube");
        cut.Find($"[data-tarjeta-lista={ids[^1]}] button[aria-label^='Bajar']").HasAttribute("disabled").Should().BeTrue("la última no baja");
        ids[1].Should().Be("k-ext");
        cut.Find("[data-tarjeta-lista=k-ext] button[aria-label^='Bajar']").HasAttribute("disabled").Should().BeTrue("una tarjeta sin marcar no se mueve");
        cut.Find("[data-tarjeta-lista=k-ext] button[aria-label^='Subir']").HasAttribute("disabled").Should().BeTrue("una tarjeta sin marcar no se mueve");
    }

    [Fact]
    public void Arrastrar_una_tarjeta_sobre_otra_la_coloca_antes()
    {
        ModuloPreferencias();
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Personalizar esta pestaña").Click();

        cut.Find("[data-tarjeta-lista=k-centros]").TriggerEvent("ondragstart", new Microsoft.AspNetCore.Components.Web.DragEventArgs());
        cut.Find("[data-tarjeta-lista=k-fact]").TriggerEvent("ondrop", new Microsoft.AspNetCore.Components.Web.DragEventArgs());

        cut.FindAll("[data-tarjeta-lista]").First().GetAttribute("data-tarjeta-lista").Should().Be("k-centros");
    }

    [Fact]
    public void Restablecer_la_pestaña_devuelve_el_orden_y_las_tarjetas_ocultas()
    {
        ModuloPreferencias("{\"Ocultas\":[\"k-ext\"],\"Orden\":{\"general\":[\"k-ext\",\"k-fact\"]},\"Etiquetas\":false,\"AvisosVistos\":[]}");
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);
        cut.WaitForAssertion(() => cut.FindAll("[data-kpi=extranjeros]").Should().BeEmpty());

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Personalizar esta pestaña").Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Restablecer esta pestaña").Click();

        cut.FindAll("[data-kpi=extranjeros]").Should().HaveCount(1);
        cut.FindAll("[data-kpi]").First().GetAttribute("data-kpi").Should().Be("facturacion");
    }

    [Fact]
    public void El_aviso_de_cierre_se_descarta_y_se_recuerda_en_este_navegador()
    {
        var modulo = ModuloPreferencias();
        var cut = Pintar(Cuadro(EstadoCifraMensual.Existe), Roles.DireccionCae, out _);
        cut.FindAll(".cuadro-aviso-cierre").Should().HaveCount(1);

        cut.Find(".cuadro-aviso-cierre button").Click();

        cut.FindAll(".cuadro-aviso-cierre").Should().BeEmpty();
        modulo.Invocations["guardar"].Last().Arguments[1]!.ToString().Should().Contain("AvisosVistos").And.MatchRegex("\\d{4}-\\d+");
    }

    [Fact]
    public void Un_aviso_ya_visto_no_vuelve_a_salir()
    {
        var hoy = DiaDeNegocio.Hoy();
        ModuloPreferencias($"{{\"Ocultas\":[],\"Orden\":{{}},\"Etiquetas\":false,\"AvisosVistos\":[\"{hoy.Year}-{hoy.Month}\"]}}");

        var cut = Pintar(Cuadro(EstadoCifraMensual.Existe), Roles.DireccionCae, out _);

        cut.WaitForAssertion(() => cut.FindAll(".cuadro-aviso-cierre").Should().BeEmpty());
    }

    [Fact]
    public void Sin_almacenamiento_la_pantalla_sigue_con_los_valores_por_defecto()
    {
        var modulo = JSInterop.SetupModule("./js/cuadro-direccion.js");
        modulo.Mode = JSRuntimeMode.Loose;
        modulo.Setup<string?>("leer", _ => true).SetException(new Microsoft.JSInterop.JSException("almacenamiento bloqueado"));

        var cut = Pintar(Cuadro(EstadoCifraMensual.Existe), Roles.DireccionCae, out _);

        cut.FindAll(".cuadro-aviso-cierre").Should().HaveCount(1);
        cut.FindAll("[data-kpi]").Should().NotBeEmpty();
    }

    // ---------------------------------------------------------------- exportar

    [Fact]
    public void Exportar_la_tabla_de_Empresas_descarga_un_CSV_con_lo_que_se_ve()
    {
        var modulo = ModuloPreferencias();
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());
        IrA(cut, "Empresas");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Exportar a Excel").Click();

        var descarga = modulo.Invocations["descargar"].Single();
        descarga.Arguments[0]!.ToString().Should().StartWith("cuadro-direccion-empresas-").And.EndWith(".csv");
        var csv = descarga.Arguments[1]!.ToString()!;
        csv.Should().Contain("Empresa;Trab.").And.Contain("Refrielectric S.A.").And.Contain("Montajes Ebro S.L.");
    }

    [Fact]
    public void El_CSV_deja_rastro_de_la_exportacion_antes_de_descargar()
    {
        var modulo = ModuloPreferencias();
        Registro.DescargasHechas = () => modulo.Invocations["descargar"].Count;
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());
        IrA(cut, "Empresas");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Exportar a Excel").Click();

        var llamada = Registro.Llamadas.Should().ContainSingle().Subject;
        llamada.Entidad.Should().Be("CuadroDireccion");
        llamada.Criterios.Should().Contain("tabla", "empresas").And.ContainKey("anyo").And.ContainKey("mes");
        llamada.DescargasHechas.Should().Be(0, "el rastro se deja antes de que el fichero salga");
        var csv = modulo.Invocations["descargar"].Single().Arguments[1]!.ToString()!;
        llamada.Filas.Should().Be(csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1, "cuenta filas de datos, no la cabecera");
    }

    [Fact]
    public void Si_el_rastro_no_se_guarda_el_CSV_no_se_descarga()
    {
        var modulo = ModuloPreferencias();
        Registro.Fallo = new InvalidOperationException("auditoría no disponible");
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());
        IrA(cut, "Empresas");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Exportar a Excel").Click();

        modulo.Invocations["descargar"].Should().BeEmpty("sin rastro no hay fichero (fallo cerrado)");
        cut.Find("[data-aviso=exportar]").TextContent.Should().Contain("constancia");
    }

    [Fact]
    public void Bajo_Sesion_Privilegiada_el_CSV_no_se_registra_ni_se_descarga()
    {
        var modulo = ModuloPreferencias();
        // Bajo Sesión Privilegiada el middleware quita los roles de negocio: la sesión llega sin rol.
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa(), rol: "SesionPrivilegiadaSinRol");
        IrA(cut, "Empresas");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Exportar a Excel").Click();

        modulo.Invocations["descargar"].Should().BeEmpty();
        Registro.Llamadas.Should().BeEmpty();
        cut.Find("[data-aviso=exportar]").TextContent.Should().Contain("Sesión Privilegiada");
    }

    // ---------------------------------------------------------------- tooltips y accesibilidad de los gráficos

    [Fact]
    public void Cada_zona_del_grafico_lleva_su_tooltip_con_los_valores_del_mes()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());

        var tooltip = cut.Find("[data-grafico=evolucion] .gs-zona .gs-tip");

        tooltip.GetAttribute("role").Should().Be("tooltip");
        tooltip.TextContent.Should().Contain("Facturación:").And.Contain("Gestiones:");
    }
}
