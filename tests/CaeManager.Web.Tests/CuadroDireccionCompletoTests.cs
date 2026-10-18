using Bunit;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Facturacion;
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

    private static LineaFacturacionCuadroDto Linea(ConceptoFacturable concepto, string nombre, int unidades, decimal importe, string moneda = "EUR") =>
        new(concepto, nombre, moneda, unidades, importe);

    private static IReadOnlyList<LineaFacturacionCuadroDto> LineasDeTarifa() =>
        [Linea(ConceptoFacturable.DocumentoGestionado, "Documento gestionado", 100, 150m), Linea(ConceptoFacturable.TrabajadorActivo, "Trabajador activo", 300, 600m)];

    private IRenderedComponent<CuadroDireccionPagina> PintarConSerie(
        CuadroDireccionDto cuadro, IReadOnlyList<LineaFacturacionCuadroDto>? lineas, string rol = Roles.DireccionCae) =>
        Pintar(cuadro, rol, out _, serie: _ => SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 30), lineas));

    // ---------------------------------------------------------------- Gestiones del mes

    [Fact]
    public void El_KPI_Gestiones_del_mes_enseña_las_gestiones_completadas_y_no_inventa_si_faltan()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);

        cut.Find("[data-kpi=gestiones] .cuadro-kpi-valor").TextContent.Trim().Should().Be("42");
        cut.Find("[data-kpi=gestiones] .cuadro-kpi-sub").TextContent.Should().Contain("410 documentos").And.Contain("37 requerimientos");
    }

    [Fact]
    public void Sin_gestiones_en_las_cifras_el_KPI_sale_vacio()
    {
        var cuadro = Cuadro(EstadoCifraMensual.Parcial);
        var sin = cuadro with { Total = cuadro.Total with { Gestiones = null } };

        var cut = Pintar(sin, Roles.DireccionCae, out _);

        cut.Find("[data-kpi=gestiones] .cuadro-kpi-valor").TextContent.Trim().Should().Be("—");
    }

    [Fact]
    public void Los_KPI_de_contrato_y_cuota_salen_con_el_estado_vacio_de_la_maqueta()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);

        foreach (var kpi in new[] { "revisar", "sintarifa" })
        {
            var tarjeta = cut.Find($"[data-kpi={kpi}]");
            tarjeta.GetAttribute("data-sin-modelo").Should().Be("contrato");
            tarjeta.QuerySelector(".cuadro-kpi-valor")!.TextContent.Trim().Should().Be("—");
        }
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
        IrA(cut, "Facturación");
        cut.Find("[data-sin-modelo=tarifas]").TextContent.Should().Contain("Todavía no hay tarifas por Empresa");
        cut.FindAll("[data-grafico=apilado]").Should().BeEmpty();
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
        cut.Find("[data-grafico=evolucion]").QuerySelectorAll("text.gs-valor").Length.Should().Be(0);

        cut.Find("#cuadro-etiquetas").Click();

        cut.Find("#cuadro-etiquetas").GetAttribute("aria-checked").Should().Be("true");
        cut.Find("[data-grafico=evolucion]").QuerySelectorAll("text.gs-valor").Length.Should().Be(6);
    }

    [Fact]
    public void Una_Empresa_con_cumplimiento_bajo_sale_en_Que_requiere_una_decision()
    {
        var cuadro = Cuadro(EstadoCifraMensual.Parcial);
        var baja = cuadro.Tenants[1] with { Cifras = cuadro.Tenants[1].Cifras with { Cumplimiento = new CumplimientoActualDto(1, 0, 0, 5, 0, 0, 0, 62, false, DateTime.UtcNow) } };
        cuadro = cuadro with { Tenants = [cuadro.Tenants[0], baja] };

        var cut = Pintar(cuadro, Roles.DireccionCae, out _);

        var alerta = cut.Find(".cuadro-alerta");
        alerta.TextContent.Should().Contain("Montajes Ebro S.L.").And.Contain("62 %");
        alerta.GetAttribute("data-prioridad").Should().Be("1");
    }

    // ---------------------------------------------------------------- Facturación

    [Fact]
    public void La_pestaña_Facturacion_pinta_el_apilado_por_concepto_el_volumen_sin_tarifa_y_el_borrador()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());

        IrA(cut, "Facturación");

        cut.Find("[data-grafico=apilado]").QuerySelectorAll(".gs-leyenda span").Length.Should().Be(2);
        cut.Find("[data-grafico=sintarifa]").Should().NotBeNull();
        cut.Find("[data-tarjeta=f-sintarifa] [data-sin-modelo=contrato]").TextContent.Should().Contain("contrato");
        cut.Find($"[data-tabla=borrador] tr[data-empresa='{TenantA}']").TextContent.Should().Contain("€");
        cut.Find($"[data-tabla=borrador] tr[data-empresa='{TenantA}'] [data-sin-modelo=contrato]").TextContent.Trim().Should().Be("—");
    }

    [Fact]
    public void El_simulador_multiplica_el_precio_por_las_altas_de_centro()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), []);
        IrA(cut, "Facturación");

        cut.Find("[data-sim=mes]").TextContent.Should().Contain("100").And.Contain("€");

        cut.Find("[data-tarjeta=f-simulador] input").Input("70");

        cut.WaitForAssertion(() => cut.Find("[data-sim=mes]").TextContent.Should().Contain("140"), TimeSpan.FromSeconds(3));
        cut.Find("[data-sim=ventana]").TextContent.Should().Contain("840");
    }

    // ---------------------------------------------------------------- Empresas

    [Fact]
    public void La_tabla_de_Empresas_trae_las_columnas_de_la_maqueta_y_vacias_las_que_no_tienen_modelo()
    {
        var cut = PintarConSerie(Cuadro(EstadoCifraMensual.Parcial), LineasDeTarifa());

        IrA(cut, "Empresas");

        var cabeceras = cut.FindAll("[data-tabla=empresas] th").Select(t => t.TextContent.Trim()).ToList();
        cabeceras.Should().Equal("Empresa", "Responde", "Trab.", "Centros", "Emp. tit.", "Extranj.", "Rotación", "Factura mes", "€ / gestión", "Cumpl.");
        var fila = cut.Find($"tr[data-empresa='{TenantA}']");
        fila.QuerySelector("td[data-sin-modelo=contrato]")!.TextContent.Trim().Should().Be("—");
        fila.TextContent.Should().Contain("Marta Gestora");
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

        cut.FindAll("[data-grafico=rendimiento] g.gd-punto").Count.Should().Be(2);
        cut.Find("[data-grafico=pareto]").QuerySelectorAll("polyline.gs-linea").Length.Should().Be(1);
        cut.Find("[data-tarjeta=cl-contratos] [data-sin-modelo=contrato]").TextContent.Should().Contain("Todavía no hay contrato");
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
        var dias = new GestionesPorDiaCuadroDireccionDto(
            [new GestionesPorDiaTenantDto(TenantA, "Refrielectric S.A.", [new GestionesDelDiaDto(new DateOnly(hoy.Year, hoy.Month, 1), 5)])], 0);
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

        mediador.SeriesPedidas.Should().ContainSingle(q => q.Meses == 6 && q.ConFacturacion);

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

        cut.FindAll("[data-sin-modelo=vacaciones]").Count.Should().Be(2);
        cut.Markup.Should().Contain("Capacidad del equipo ausente por vacaciones").And.Contain("Saldo de vacaciones");
        cut.Markup.Should().NotContain("Vacaciones del año por");
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
                [Linea(ConceptoFacturable.DocumentoGestionado, "Documento gestionado", 10, d.Year == hoy.Year ? 200m : 100m)]))
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
    public void Sin_un_mismo_mes_en_dos_años_la_comparativa_dice_que_aparece_a_partir_del_mes_trece()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _,
            serie: q => q.Meses == 24 ? DosAnyos(false) : SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 25)));

        IrA(cut, "Comparativa anual");

        cut.WaitForAssertion(() => cut.Find("[data-tarjeta=cmp-linea] [data-sin-modelo=historico]").TextContent.Should().Contain("trece meses"));
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

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Personalizar").Click();
        cut.Find("[data-tarjeta-lista=k-ext] input[type=checkbox]").Change(false);

        cut.FindAll("[data-kpi=extranjeros]").Should().BeEmpty();
        var guardado = modulo.Invocations["guardar"].Last().Arguments[1]!.ToString()!;
        guardado.Should().Contain("k-ext");
        modulo.Invocations["guardar"].Last().Arguments[0].Should().Be($"cuadro-direccion:preferencias:{UsuarioPrueba}",
            "la clave lleva el usuario: dos personas en el mismo navegador no comparten preferencias");
        modulo.Invocations["leer"].Single().Arguments[0].Should().Be($"cuadro-direccion:preferencias:{UsuarioPrueba}");
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

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Exportar tabla").Click();

        cut.Find("[data-aviso=exportar]").TextContent.Should().Contain("No se pudo exportar");
    }

    [Fact]
    public void Las_flechas_reordenan_las_tarjetas_de_la_pestaña()
    {
        ModuloPreferencias();
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Personalizar").Click();
        var antes = cut.FindAll("[data-tarjeta-lista]").Select(l => l.GetAttribute("data-tarjeta-lista")).ToList();
        antes.Take(2).Should().Equal("k-fact", "k-ext");

        cut.Find("[data-tarjeta-lista=k-ext] button[aria-label^='Subir']").Click();

        cut.FindAll("[data-tarjeta-lista]").Select(l => l.GetAttribute("data-tarjeta-lista")).Take(2).Should().Equal("k-ext", "k-fact");
    }

    [Fact]
    public void Arrastrar_una_tarjeta_sobre_otra_la_coloca_antes()
    {
        ModuloPreferencias();
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Personalizar").Click();

        cut.Find("[data-tarjeta-lista=k-gestiones]").TriggerEvent("ondragstart", new Microsoft.AspNetCore.Components.Web.DragEventArgs());
        cut.Find("[data-tarjeta-lista=k-fact]").TriggerEvent("ondrop", new Microsoft.AspNetCore.Components.Web.DragEventArgs());

        cut.FindAll("[data-tarjeta-lista]").First().GetAttribute("data-tarjeta-lista").Should().Be("k-gestiones");
    }

    [Fact]
    public void Restablecer_la_pestaña_devuelve_el_orden_y_las_tarjetas_ocultas()
    {
        ModuloPreferencias("{\"Ocultas\":[\"k-ext\"],\"Orden\":{\"general\":[\"k-ext\",\"k-fact\"]},\"Etiquetas\":false,\"AvisosVistos\":[]}");
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);
        cut.WaitForAssertion(() => cut.FindAll("[data-kpi=extranjeros]").Should().BeEmpty());

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Personalizar").Click();
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

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Exportar tabla").Click();

        var descarga = modulo.Invocations["descargar"].Single();
        descarga.Arguments[0]!.ToString().Should().StartWith("cuadro-direccion-empresas-").And.EndWith(".csv");
        var csv = descarga.Arguments[1]!.ToString()!;
        csv.Should().Contain("Empresa;Responde;Trab.").And.Contain("Refrielectric S.A.").And.Contain("Montajes Ebro S.L.");
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
