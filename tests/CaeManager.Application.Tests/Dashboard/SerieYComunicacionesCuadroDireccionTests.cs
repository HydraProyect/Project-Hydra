using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Dashboard;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Dashboard;

/// <summary>Las piezas puras de la serie mensual, de Gestiones en las cifras y de Comunicaciones del Cuadro de dirección.</summary>
public class SerieYComunicacionesCuadroDireccionTests
{
    private static readonly DateTime EnOctubre = new(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc);

    private static ClienteAutorizadoDto Tenant(string nombre) => new(Guid.NewGuid(), nombre, EsOrigen: false);

    private static MedicionCuadroDireccion Medicion(int altas = 0, int? gestiones = null, EstadoCifraMensual? persistido = null) =>
        new(10, altas, 0, 2, 0, 0, 0, [], new KpisDashboardDto(0, 0, 0, 0, 0, 0, 0, 0), gestiones, persistido);

    private static LineaFacturacionCuadroDto Linea(ConceptoTarifaOperadorCae concepto, int unidades, decimal precio, string moneda = "EUR") =>
        new(concepto, concepto.ToString(), moneda, unidades, unidades * precio);

    // ---------------------------------------------------------------- Gestiones en las cifras

    [Fact]
    public void Las_cifras_suman_las_gestiones_completadas_de_los_Tenants()
    {
        var ibertec = Tenant("Ibertec");
        var eco = Tenant("EcoPlant");

        var cuadro = ObtenerCuadroDireccionQueryHandler.Fusionar(
            new MesCuadroDireccion(2026, 10), EnOctubre,
            [(ibertec, Medicion(gestiones: 30)), (eco, Medicion(gestiones: 12))],
            new Dictionary<Guid, PrincipalDeCarteraDto>(), 0);

        cuadro.Total.Gestiones!.Valor.Should().Be(42);
        cuadro.Total.Gestiones.Estado.Should().Be(EstadoCifraMensual.Parcial);
        cuadro.Tenants.Single(t => t.TenantId == eco.TenantId).Cifras.Gestiones!.Valor.Should().Be(12);
    }

    [Fact]
    public void Si_alguna_medicion_no_lleva_gestiones_la_cifra_no_se_inventa()
    {
        var cuadro = ObtenerCuadroDireccionQueryHandler.Fusionar(
            new MesCuadroDireccion(2026, 10), EnOctubre,
            [(Tenant("Ibertec"), Medicion(gestiones: 30)), (Tenant("EcoPlant"), Medicion(gestiones: null))],
            new Dictionary<Guid, PrincipalDeCarteraDto>(), 0);

        cuadro.Total.Gestiones.Should().BeNull("un cierre anterior a este campo no la guardó");
    }

    [Fact]
    public void Sin_Tenants_las_gestiones_son_null()
    {
        ObtenerCuadroDireccionQueryHandler.SinCifras(2026, 10, null, EnOctubre, CuadroDireccionSinCifras.MesNoValido)
            .Total.Gestiones.Should().BeNull();
    }

    // ---------------------------------------------------------------- meses de la serie

    [Fact]
    public void La_serie_va_del_mes_mas_antiguo_al_pedido()
    {
        var meses = ObtenerSerieCuadroDireccionQueryHandler.MesesDeLaSerie(2026, 10, 6, EnOctubre);

        meses.Select(m => (m.Anyo, m.Mes)).Should().Equal((2026, 5), (2026, 6), (2026, 7), (2026, 8), (2026, 9), (2026, 10));
    }

    [Fact]
    public void La_serie_cruza_el_cambio_de_anyo_y_acota_la_cantidad()
    {
        ObtenerSerieCuadroDireccionQueryHandler.MesesDeLaSerie(2026, 2, 4, EnOctubre)
            .Select(m => (m.Anyo, m.Mes)).Should().Equal((2025, 11), (2025, 12), (2026, 1), (2026, 2));

        ObtenerSerieCuadroDireccionQueryHandler.MesesDeLaSerie(2026, 10, 500, EnOctubre)
            .Should().HaveCount(ObtenerSerieCuadroDireccionQuery.MaximoMeses);
    }

    [Fact]
    public void La_serie_descarta_meses_futuros_o_no_validos()
    {
        ObtenerSerieCuadroDireccionQueryHandler.MesesDeLaSerie(2026, 12, 3, EnOctubre)
            .Select(m => m.Mes).Should().Equal(10);
        ObtenerSerieCuadroDireccionQueryHandler.MesesDeLaSerie(2026, 13, 3, EnOctubre).Should().BeEmpty();
        ObtenerSerieCuadroDireccionQueryHandler.MesesDeLaSerie(2000, 1, 3, EnOctubre)
            .Select(m => (m.Anyo, m.Mes)).Should().Equal((2000, 1));
    }

    // ---------------------------------------------------------------- construir la serie

    [Fact]
    public void Construir_da_un_punto_por_mes_por_Tenant_y_en_total_con_el_estado_del_mes()
    {
        var ibertec = Tenant("Ibertec");
        var eco = Tenant("EcoPlant");
        var meses = ObtenerSerieCuadroDireccionQueryHandler.MesesDeLaSerie(2026, 10, 2, EnOctubre);

        var serie = ObtenerSerieCuadroDireccionQueryHandler.Construir(meses, EnOctubre,
        [
            (ibertec, [new MesMedidoCuadro(Medicion(altas: 3, gestiones: 5), null), new MesMedidoCuadro(Medicion(altas: 4, gestiones: 6), null)]),
            (eco, [new MesMedidoCuadro(Medicion(altas: 1, gestiones: 2), null), new MesMedidoCuadro(Medicion(altas: 2, gestiones: 3), null)]),
        ], tenantsSinAlcanceCompleto: 1);

        serie.TenantsSinAlcanceCompleto.Should().Be(1);
        serie.Tenants.Select(t => t.TenantNombre).Should().Equal("EcoPlant", "Ibertec");
        serie.Total.Select(p => p.Cifras.AltasTrabajadores.Valor).Should().Equal(4, 6);
        serie.Total.Select(p => p.Cifras.Gestiones!.Valor).Should().Equal(7, 9);
        serie.Total[0].Estado.Should().Be(EstadoCifraMensual.Estimado, "septiembre ya terminó y no tiene cierre");
        serie.Total[1].Estado.Should().Be(EstadoCifraMensual.Parcial);
        serie.Total[0].Facturacion.Should().BeNull("la serie no se pidió con facturación");
    }

    [Fact]
    public void Un_mes_con_cierre_vigente_en_todos_los_Tenants_sale_cerrado()
    {
        var meses = ObtenerSerieCuadroDireccionQueryHandler.MesesDeLaSerie(2026, 10, 1, new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc));

        var serie = ObtenerSerieCuadroDireccionQueryHandler.Construir(meses, new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc),
            [(Tenant("Ibertec"), [new MesMedidoCuadro(Medicion(persistido: EstadoCifraMensual.Existe), null)])], 0);

        serie.Total.Single().Estado.Should().Be(EstadoCifraMensual.Existe);
        serie.Tenants.Single().Meses.Single().Estado.Should().Be(EstadoCifraMensual.Existe);
    }

    // ---------------------------------------------------------------- facturación estimada

    [Fact]
    public void Sumar_lineas_agrupa_por_concepto_y_moneda_sin_mezclar_monedas()
    {
        var lineas = ObtenerSerieCuadroDireccionQueryHandler.SumarLineas(
        [
            Linea(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 10, 2m),
            Linea(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 5, 2m),
            Linea(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 3, 1m, "USD"),
            Linea(ConceptoTarifaOperadorCae.CentroNuevo, 2, 50m),
        ]);

        lineas.Should().HaveCount(3);
        var trabajadoresEur = lineas.Single(l => l.Concepto == ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero && l.MonedaIso == "EUR");
        trabajadoresEur.Unidades.Should().Be(15);
        trabajadoresEur.Importe.Should().Be(30m);
        lineas.Single(l => l.MonedaIso == "USD").Importe.Should().Be(3m);
    }

    [Fact]
    public void Con_facturacion_cada_punto_suma_las_lineas_de_todos_los_Tenants()
    {
        var meses = ObtenerSerieCuadroDireccionQueryHandler.MesesDeLaSerie(2026, 10, 1, EnOctubre);
        var a = ObtenerSerieCuadroDireccionQueryHandler.SumarLineas([Linea(ConceptoTarifaOperadorCae.ObraNueva, 10, 1.5m)]);
        var b = ObtenerSerieCuadroDireccionQueryHandler.SumarLineas([Linea(ConceptoTarifaOperadorCae.ObraNueva, 4, 1.5m)]);

        var serie = ObtenerSerieCuadroDireccionQueryHandler.Construir(meses, EnOctubre,
        [
            (Tenant("Ibertec"), [new MesMedidoCuadro(Medicion(), a)]),
            (Tenant("EcoPlant"), [new MesMedidoCuadro(Medicion(), b)]),
        ], 0);

        var punto = serie.Total.Single();
        punto.ImporteEn("EUR").Should().Be(21m);
        punto.UnidadesDe(ConceptoTarifaOperadorCae.ObraNueva).Should().Be(14);
        punto.UnidadesDe(ConceptoTarifaOperadorCae.CentroNuevo).Should().Be(0, "sin tarifa no hay línea y no se inventa");
    }

    [Fact]
    public void Un_Tenant_sin_tarifas_tiene_facturacion_vacia_y_no_null()
    {
        var meses = ObtenerSerieCuadroDireccionQueryHandler.MesesDeLaSerie(2026, 10, 1, EnOctubre);

        var serie = ObtenerSerieCuadroDireccionQueryHandler.Construir(meses, EnOctubre,
            [(Tenant("Ibertec"), [new MesMedidoCuadro(Medicion(), ObtenerSerieCuadroDireccionQueryHandler.SumarLineas([]))])], 0);

        serie.Total.Single().Facturacion.Should().BeEmpty();
        serie.Total.Single().ImporteEn("EUR").Should().Be(0m);
    }

    // ---------------------------------------------------------------- Comunicaciones

    [Fact]
    public void Comunicaciones_suma_por_estado_y_canal_y_deja_los_ceros()
    {
        var uno = new ComunicacionesTenantDto(Guid.NewGuid(), "Ibertec",
            [new ConteoPorEstadoDto(EstadoConversacion.Abierta, 20), new ConteoPorEstadoDto(EstadoConversacion.EnEsperaDeTercero, 2)],
            [new ConteoPorCanalDto(CanalConversacion.Correo, 40)]);
        var otro = new ComunicacionesTenantDto(Guid.NewGuid(), "EcoPlant",
            [new ConteoPorEstadoDto(EstadoConversacion.Abierta, 5)],
            [new ConteoPorCanalDto(CanalConversacion.Correo, 7), new ConteoPorCanalDto(CanalConversacion.WhatsApp, 9)]);

        var resultado = ObtenerComunicacionesCuadroDireccionQueryHandler.Fusionar(2026, 10, [uno, otro], 2);

        resultado.Tenants.Select(t => t.TenantNombre).Should().Equal("EcoPlant", "Ibertec");
        resultado.TotalPorEstado.Single(e => e.Estado == EstadoConversacion.Abierta).Conversaciones.Should().Be(25);
        resultado.TotalPorEstado.Single(e => e.Estado == EstadoConversacion.EsperandoRespuestaExterna).Conversaciones.Should().Be(0);
        resultado.TotalPorEstado.Select(e => e.Estado).Should().BeEquivalentTo(ObtenerComunicacionesCuadroDireccionQueryHandler.EstadosAbiertos);
        resultado.TotalPorCanal.Single(c => c.Canal == CanalConversacion.Correo).Solicitudes.Should().Be(47);
        resultado.TotalPorCanal.Single(c => c.Canal == CanalConversacion.WhatsApp).Solicitudes.Should().Be(9);
        resultado.TenantsSinAlcanceCompleto.Should().Be(2);
    }

    // ---------------------------------------------------------------- Autoridad sobre los importes

    private sealed class UsuarioConRol(string? rol) : CaeManager.Application.Common.ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(rol);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    [Fact]
    public async Task Direccion_CAE_del_Operador_pasa_la_guarda_de_los_importes_de_la_serie()
    {
        (await CaeManager.Application.Dashboard.TarifasOperadorCae.AutorizacionTarifasOperadorCae
            .OperadorQueGestionaAsync(new UsuarioConRol("DireccionCae"))).Should().NotBeNull();
    }

    [Fact]
    public async Task Coordinador_CAE_lee_el_Cuadro_pero_no_pasa_la_guarda_de_los_importes()
    {
        await AutorizacionCuadroDireccionComprobar("CoordinadorCae");
        (await CaeManager.Application.Dashboard.TarifasOperadorCae.AutorizacionTarifasOperadorCae
            .OperadorQueGestionaAsync(new UsuarioConRol("CoordinadorCae"))).Should().BeNull();
    }

    private static async Task AutorizacionCuadroDireccionComprobar(string rol) =>
        await CaeManager.Application.Dashboard.AutorizacionCuadroDireccion.ExigirLecturaAsync(new UsuarioConRol(rol));

    // ---------------------------------------------------------------- Gestiones por día

    [Fact]
    public void Las_gestiones_por_dia_se_agrupan_por_dia_de_negocio_peninsular_y_van_ordenadas()
    {
        var resultado = ObtenerGestionesPorDiaCuadroDireccionQueryHandler.PorDia(
        [
            new DateTime(2026, 10, 17, 22, 30, 0, DateTimeKind.Utc), // 00:30 del 18 en Madrid
            new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 16, 8, 0, 0, DateTimeKind.Unspecified),
        ]);

        resultado.Should().Equal(
            new GestionesDelDiaDto(new DateOnly(2026, 10, 16), 1),
            new GestionesDelDiaDto(new DateOnly(2026, 10, 18), 2));
    }

    [Fact]
    public void Sumar_gestiones_por_dia_junta_Tenants_o_filtra_uno_solo()
    {
        var uno = Guid.NewGuid();
        var otro = Guid.NewGuid();
        var dia = new DateOnly(2026, 10, 5);
        var dto = new GestionesPorDiaCuadroDireccionDto(
        [
            new GestionesPorDiaTenantDto(uno, "Uno", [new GestionesDelDiaDto(dia, 3), new GestionesDelDiaDto(dia.AddDays(1), 1)]),
            new GestionesPorDiaTenantDto(otro, "Otro", [new GestionesDelDiaDto(dia, 4)]),
        ], 0);

        dto.Sumar().Should().Equal(new GestionesDelDiaDto(dia, 7), new GestionesDelDiaDto(dia.AddDays(1), 1));
        dto.Sumar(otro).Should().Equal(new GestionesDelDiaDto(dia, 4));
        dto.Sumar(Guid.NewGuid()).Should().BeEmpty();
    }

    [Fact]
    public void Comunicaciones_no_cuenta_resueltas_ni_cerradas_como_abiertas()
    {
        ObtenerComunicacionesCuadroDireccionQueryHandler.EstadosAbiertos
            .Should().NotContain([EstadoConversacion.Resuelta, EstadoConversacion.Cerrada]);
    }
}
