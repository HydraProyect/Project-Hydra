using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Incidencias;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Dashboard;

public class ObtenerDashboardEjecutivoQueryHandlerTests
{
    private static ClienteAutorizadoDto Cliente(string nombre) => new(Guid.NewGuid(), nombre, EsOrigen: false);

    private static CatalogoKpisValoresDto Valores(
        int trabajadoresActivos = 0, int centros = 0, int visitas = 0,
        int vigentes = 0, int proximos = 0, int urgentes = 0, int vencidos = 0,
        double? porcentajeCumplimientoDocumental = null, int totalRequeridosCumplimiento = 0,
        IReadOnlyList<CentroCumplimientoDto>? centrosConMenorCumplimiento = null,
        int incidenciasAbiertas = 0, IReadOnlyList<GravedadIncidenciaConteoDto>? incidenciasPorGravedad = null,
        double? tiempoMedioResolucionDias = null, int totalIncidenciasResueltas = 0,
        double? confianzaMediaIa = null, decimal costeIaMes = 0m, double? tiempoMedioMsIa = null, int totalAuditoriasIaMes = 0,
        decimal facturacionEstimada = 0m, KpisBpoDto? bpo = null) =>
        new(
            Documental: new KpisDashboardDto(trabajadoresActivos, centros, vencidos, urgentes, proximos, vigentes, visitas, 0),
            TotalDocumentosConVigencia: vigentes + proximos + urgentes + vencidos,
            PorcentajeCumplimientoDocumental: porcentajeCumplimientoDocumental,
            TotalRequeridosCumplimiento: totalRequeridosCumplimiento,
            CentrosConMenorCumplimiento: centrosConMenorCumplimiento ?? [],
            IncidenciasAbiertas: incidenciasAbiertas,
            IncidenciasPorGravedad: incidenciasPorGravedad ?? [],
            TiempoMedioResolucionIncidenciasDias: tiempoMedioResolucionDias,
            TotalIncidenciasResueltas: totalIncidenciasResueltas,
            ConfianzaMediaIa: confianzaMediaIa,
            CosteIaMesActual: costeIaMes,
            TiempoMedioProcesamientoIaMs: tiempoMedioMsIa,
            TotalAuditoriasIaMes: totalAuditoriasIaMes,
            FacturacionEstimadaMesActual: facturacionEstimada,
            Bpo: bpo ?? KpisBpoDto.Vacio);

    [Fact]
    public void Suma_los_conteos_de_todos_los_tenants()
    {
        var porTenant = new List<(ClienteAutorizadoDto, CatalogoKpisValoresDto)>
        {
            (Cliente("Ibertec"), Valores(trabajadoresActivos: 100, centros: 10, visitas: 5, vigentes: 80, urgentes: 20, incidenciasAbiertas: 3, costeIaMes: 12.5m, facturacionEstimada: 1000m)),
            (Cliente("EcoPlant"), Valores(trabajadoresActivos: 50, centros: 4, visitas: 2, vigentes: 40, urgentes: 10, incidenciasAbiertas: 1, costeIaMes: 5m, facturacionEstimada: 300m)),
        };

        var resultado = ObtenerDashboardEjecutivoQueryHandler.Fusionar(porTenant);

        resultado.TotalTenants.Should().Be(2);
        resultado.TrabajadoresActivos.Should().Be(150);
        resultado.Centros.Should().Be(14);
        resultado.VisitasProgramadas.Should().Be(7);
        resultado.DocumentosVigentes.Should().Be(120);
        resultado.DocumentosUrgentes.Should().Be(30);
        resultado.IncidenciasAbiertas.Should().Be(4);
        resultado.CosteIaMesActual.Should().Be(17.5m);
        resultado.FacturacionEstimadaMesActual.Should().Be(1300m);
    }

    [Fact]
    public void La_tasa_de_cumplimiento_se_recalcula_sobre_los_totales_fusionados_no_como_promedio_de_porcentajes()
    {
        // Tenant A: 9/10 vigentes (90%). Tenant B: 1/100 vigentes (1%).
        // Un promedio simple de porcentajes daría (90+1)/2 = 45.5%, pero eso
        // ignora que el tenant B aporta 10 veces más documentos — la tasa
        // fusionada correcta pondera por volumen: 10 vigentes de 110 = 9%.
        var porTenant = new List<(ClienteAutorizadoDto, CatalogoKpisValoresDto)>
        {
            (Cliente("TenantPequeño"), Valores(vigentes: 9, vencidos: 1)),
            (Cliente("TenantGrande"), Valores(vigentes: 1, vencidos: 99)),
        };

        var resultado = ObtenerDashboardEjecutivoQueryHandler.Fusionar(porTenant);

        resultado.TasaCumplimiento.Should().Be(9);
        resultado.TasaCumplimiento.Should().NotBe(45);
    }

    [Fact]
    public void Las_medias_se_ponderan_por_el_volumen_de_cada_tenant_no_por_un_promedio_simple()
    {
        // Tenant A: 100% sobre 1 par requerido. Tenant B: 0% sobre 99.
        // Promedio simple: (100+0)/2 = 50. Promedio ponderado por volumen: casi 0.
        var porTenant = new List<(ClienteAutorizadoDto, CatalogoKpisValoresDto)>
        {
            (Cliente("TenantPequeño"), Valores(porcentajeCumplimientoDocumental: 100, totalRequeridosCumplimiento: 1)),
            (Cliente("TenantGrande"), Valores(porcentajeCumplimientoDocumental: 0, totalRequeridosCumplimiento: 99)),
        };

        var resultado = ObtenerDashboardEjecutivoQueryHandler.Fusionar(porTenant);

        resultado.PorcentajeCumplimientoDocumental.Should().NotBeNull();
        resultado.PorcentajeCumplimientoDocumental!.Value.Should().BeApproximately(1.0, 0.1);
    }

    [Fact]
    public void Una_media_sin_datos_en_ningun_tenant_da_null_no_cero()
    {
        var porTenant = new List<(ClienteAutorizadoDto, CatalogoKpisValoresDto)>
        {
            (Cliente("Ibertec"), Valores()),
            (Cliente("EcoPlant"), Valores()),
        };

        var resultado = ObtenerDashboardEjecutivoQueryHandler.Fusionar(porTenant);

        resultado.PorcentajeCumplimientoDocumental.Should().BeNull();
        resultado.ConfianzaMediaIa.Should().BeNull();
        resultado.TiempoMedioResolucionIncidenciasDias.Should().BeNull();
    }

    [Fact]
    public void El_ranking_de_centros_con_menor_cumplimiento_corta_a_10_aunque_varios_tenants_aporten_mas()
    {
        var porTenant = new List<(ClienteAutorizadoDto, CatalogoKpisValoresDto)>();
        for (var t = 0; t < 4; t++)
        {
            var centros = Enumerable.Range(0, 5)
                .Select(i => new CentroCumplimientoDto(Guid.NewGuid(), $"Centro {t}-{i}", i))
                .ToList();
            porTenant.Add((Cliente($"Tenant{t}"), Valores(centrosConMenorCumplimiento: centros)));
        }

        var resultado = ObtenerDashboardEjecutivoQueryHandler.Fusionar(porTenant);

        resultado.CentrosConMenorCumplimiento.Should().HaveCount(10);
        resultado.CentrosConMenorCumplimiento.Should().BeInAscendingOrder(c => c.Porcentaje);
    }

    [Fact]
    public void Las_incidencias_por_gravedad_se_suman_por_clave_entre_tenants()
    {
        var porTenant = new List<(ClienteAutorizadoDto, CatalogoKpisValoresDto)>
        {
            (Cliente("Ibertec"), Valores(incidenciasPorGravedad:
            [
                new GravedadIncidenciaConteoDto(GravedadIncidencia.Leve, 3),
                new GravedadIncidenciaConteoDto(GravedadIncidencia.Grave, 1),
            ])),
            (Cliente("EcoPlant"), Valores(incidenciasPorGravedad:
            [
                new GravedadIncidenciaConteoDto(GravedadIncidencia.Leve, 2),
                new GravedadIncidenciaConteoDto(GravedadIncidencia.MuyGrave, 1),
            ])),
        };

        var resultado = ObtenerDashboardEjecutivoQueryHandler.Fusionar(porTenant);

        resultado.IncidenciasPorGravedad.Should().BeEquivalentTo(
        [
            new GravedadIncidenciaConteoDto(GravedadIncidencia.Leve, 5),
            new GravedadIncidenciaConteoDto(GravedadIncidencia.Grave, 1),
            new GravedadIncidenciaConteoDto(GravedadIncidencia.MuyGrave, 1),
        ]);
    }

    /// <summary>
    /// El mismo Operador Delegado puede trabajar para varios Clientes Delegantes: si su
    /// ocupación se concatenara sin agrupar, aparecería dos veces en el ranking y la
    /// Coordinación repartiría carga sobre una lista con la misma persona duplicada.
    /// </summary>
    [Fact]
    public void Agrupa_por_persona_la_ocupacion_de_un_gestor_que_trabaja_en_varios_tenants()
    {
        var gestor = Guid.NewGuid();

        var porTenant = new List<(ClienteAutorizadoDto, CatalogoKpisValoresDto)>
        {
            (Cliente("Ibertec"), Valores(bpo: KpisBpoDto.Vacio with
            {
                OcupacionPorGestor = [new OcupacionGestorDto(gestor, "Pedro Picapiedra", 3600, 10)]
            })),
            (Cliente("EcoPlant"), Valores(bpo: KpisBpoDto.Vacio with
            {
                OcupacionPorGestor = [new OcupacionGestorDto(gestor, "Pedro Picapiedra", 7200, 20)]
            })),
        };

        var resultado = ObtenerDashboardEjecutivoQueryHandler.Fusionar(porTenant);

        resultado.Bpo.OcupacionPorGestor.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new OcupacionGestorDto(gestor, "Pedro Picapiedra", 10800, 30));
    }

    /// <summary>
    /// Los porcentajes BPO se derivan del numerador y el denominador ya sumados, nunca
    /// promediando los porcentajes de cada tenant.
    /// </summary>
    [Fact]
    public void Suma_numerador_y_denominador_de_la_palanca_IA_en_vez_de_promediar_porcentajes()
    {
        var porTenant = new List<(ClienteAutorizadoDto, CatalogoKpisValoresDto)>
        {
            (Cliente("Ibertec"), Valores(bpo: KpisBpoDto.Vacio with
            {
                SugerenciasConfirmadasSinEdicion = 90, TotalSugerenciasResueltas = 100
            })),
            (Cliente("EcoPlant"), Valores(bpo: KpisBpoDto.Vacio with
            {
                SugerenciasConfirmadasSinEdicion = 0, TotalSugerenciasResueltas = 1
            })),
        };

        var resultado = ObtenerDashboardEjecutivoQueryHandler.Fusionar(porTenant);

        resultado.Bpo.SugerenciasConfirmadasSinEdicion.Should().Be(90);
        resultado.Bpo.TotalSugerenciasResueltas.Should().Be(101);
    }

    [Fact]
    public void Sin_ningun_tenant_autorizado_devuelve_valores_neutros()
    {
        var resultado = ObtenerDashboardEjecutivoQueryHandler.Fusionar([]);

        resultado.TotalTenants.Should().Be(0);
        resultado.TrabajadoresActivos.Should().Be(0);
        resultado.TasaCumplimiento.Should().Be(100, "sin documentos con vigencia, el mismo criterio que ObtenerKpisDashboardQuery aplica: 100%, no 0%");
        resultado.CentrosConMenorCumplimiento.Should().BeEmpty();
    }
}
