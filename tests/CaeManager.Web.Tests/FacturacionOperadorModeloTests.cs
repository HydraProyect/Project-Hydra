using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Dashboard;
using CaeManager.Web.Features.Facturacion;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// La composición de «Facturación» (<see cref="FacturacionOperadorModelo"/>): qué unidades se muestran, de dónde sale cada
/// importe y qué no se inventa. Las cifras son las de la maqueta (assets/facturacion-operador.js, Obras y Estructuras del Ebro).
/// No observa el cálculo del importe, que es de <c>ValoracionOperadorCae</c> y llega ya hecho en las líneas de la serie:
/// el modelo enseña lo que dice la línea (unidades, precio = importe entre unidades, moneda) y nunca relee la tarifa en otro
/// instante.
/// </summary>
public class FacturacionOperadorModeloTests
{
    private static readonly Guid Ebro = Guid.Parse("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid Reformas = Guid.Parse("e0000000-0000-0000-0000-000000000002");
    private static readonly Guid Ajena = Guid.Parse("e0000000-0000-0000-0000-000000000009");
    private static readonly DateTime Ahora = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DesdeSiempre = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const ConceptoTarifaOperadorCae Ext = ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero;
    private const ConceptoTarifaOperadorCae Centro = ConceptoTarifaOperadorCae.CentroNuevo;
    private const ConceptoTarifaOperadorCae Obra = ConceptoTarifaOperadorCae.ObraNueva;

    private static CifrasCuadroDireccionDto Cifras(int centros, int? obras, int? gestiones) => new(
        new CifraMensual(0, EstadoCifraMensual.Parcial), new CifraMensual(0, EstadoCifraMensual.Parcial),
        new CifraMensual(0, EstadoCifraMensual.Parcial), new CifraMensual(0, EstadoCifraMensual.Parcial),
        new CifraMensual(centros, EstadoCifraMensual.Parcial), new CifraMensual(0, EstadoCifraMensual.Parcial),
        new CifraMensual(0, EstadoCifraMensual.Parcial), new SolicitudesDelMesDto(new CifraMensual(0, EstadoCifraMensual.Parcial), []),
        new CumplimientoActualDto(0, 0, 0, 0, 0, 0, 0, 0, true, Ahora),
        Gestiones: gestiones is null ? null : new CifraMensual(gestiones.Value, EstadoCifraMensual.Parcial),
        ObrasNuevas: obras is null ? null : new CifraMensual(obras.Value, EstadoCifraMensual.Parcial));

    private static LineaFacturacionCuadroDto Linea(ConceptoTarifaOperadorCae c, int unidades, decimal importe, string moneda = "EUR") =>
        new(c, ValoracionOperadorCae.NombreDe(c), moneda, unidades, importe);

    private static TarifaOperadorCaeDto Tarifa(
        Guid tenant, ConceptoTarifaOperadorCae c, decimal precio, bool activa = true, string moneda = "EUR", DateTime? desde = null) =>
        new(tenant, c, precio, moneda, activa, desde ?? DesdeSiempre);

    private static SerieTenantCuadroDto Tenant(
        Guid id, string nombre, CifrasCuadroDireccionDto cifras, IReadOnlyList<LineaFacturacionCuadroDto>? facturacion,
        EstadoCifraMensual estado = EstadoCifraMensual.Parcial, int anyo = 2026, int mes = 10) =>
        new(id, nombre, [new PuntoSerieCuadroDto(anyo, mes, estado, cifras, facturacion)]);

    /// <summary>El total de la serie lleva el estado del primer Tenant (o el pedido): es de donde sale el estado del mes.</summary>
    private static SerieCuadroDireccionDto Serie(int sinAlcance, params SerieTenantCuadroDto[] tenants) =>
        SerieConTotal(sinAlcance, tenants.FirstOrDefault()?.Meses[0].Estado ?? EstadoCifraMensual.Parcial, tenants);

    private static SerieCuadroDireccionDto SerieConTotal(int sinAlcance, EstadoCifraMensual estadoTotal, params SerieTenantCuadroDto[] tenants)
    {
        var mes = tenants.FirstOrDefault()?.Meses[0];
        var total = mes is null ? [] : new[] { mes with { Estado = estadoTotal, Facturacion = null } };
        return new SerieCuadroDireccionDto(total, tenants, sinAlcance);
    }

    private static ConteoExtranjerosCobrablesDelMesDto Extranjeros(params (Guid Tenant, int Asignaciones)[] porTenant) =>
        new(2026, 10, porTenant.Select(t => new ConteoExtranjerosCobrablesTenantDto(t.Tenant, "x", t.Asignaciones, t.Asignaciones)).ToList(), 0);

    /// <summary>Ebro de la maqueta: 14 extranjeros a 40, 2 Centros a 25 y 1 Obra a 120 = 730 €, con 212 gestiones.</summary>
    private static (SerieTenantCuadroDto Tenant, ConteoExtranjerosCobrablesDelMesDto Extranjeros, List<TarifaOperadorCaeDto> Tarifas) EbroDeLaMaqueta() => (
        Tenant(Ebro, "Obras y Estructuras del Ebro S.L.", Cifras(centros: 2, obras: 1, gestiones: 212),
            [Linea(Ext, 14, 560m), Linea(Centro, 2, 50m), Linea(Obra, 1, 120m)]),
        Extranjeros((Ebro, 14)),
        [Tarifa(Ebro, Ext, 40m), Tarifa(Ebro, Centro, 25m), Tarifa(Ebro, Obra, 120m)]);

    /// <summary>Sin lista explícita, el Operador opera a todas las Empresas de la serie.</summary>
    private static ModeloFacturacion Construir(
        SerieCuadroDireccionDto serie, ConteoExtranjerosCobrablesDelMesDto extranjeros, IReadOnlyList<TarifaOperadorCaeDto>? tarifas,
        IReadOnlyCollection<Guid>? operadas = null, int anyo = 2026, int mes = 10) =>
        FacturacionOperadorModelo.Construir(
            anyo, mes, serie, extranjeros, tarifas, operadas ?? serie.Tenants.Select(t => t.TenantId).ToList(), Ahora);

    [Fact]
    public void La_cuota_es_la_suma_de_las_lineas_valoradas_y_las_tres_unidades_se_muestran()
    {
        var (tenant, extranjeros, tarifas) = EbroDeLaMaqueta();

        var empresa = Construir(Serie(0, tenant), extranjeros, tarifas).Empresas.Single();

        empresa.Extranjeros.Should().Be(14);
        empresa.CentrosNuevos.Should().Be(2);
        empresa.ObrasNuevas.Should().Be(1);
        empresa.Cuota.Should().Equal(new ImporteEnMoneda("EUR", 730m));
        empresa.Concepto(Ext).Precio.Should().Be(40m);
        empresa.Concepto(Centro).Importe.Should().Be(50m);
        empresa.SinTarifa.Should().BeFalse();
        empresa.SinTarifaEnElMes.Should().BeFalse();
        empresa.ImportesOcultos.Should().BeFalse();
    }

    [Fact]
    public void Ingreso_por_gestion_y_relacion_son_las_de_la_maqueta()
    {
        var (tenant, extranjeros, tarifas) = EbroDeLaMaqueta();

        var empresa = Construir(Serie(0, tenant), extranjeros, tarifas).Empresas.Single();

        empresa.Gestiones.Should().Be(212);
        empresa.GestionesExtranjero.Should().Be(14);
        empresa.IngresoPorGestion.Should().BeApproximately(730.0 / 212, 1e-9);
        empresa.CostoPorGestion.Should().BeApproximately(170.0 / 198, 1e-9);
        empresa.CostoPorExtranjero.Should().BeApproximately(40.0, 1e-9);
        empresa.Relacion.Should().BeApproximately(40.0 / (170.0 / 198), 1e-9);
    }

    [Fact]
    public void Con_linea_las_unidades_son_las_de_la_linea_y_unidades_por_precio_dan_el_importe_aunque_el_Excel_cuente_otras()
    {
        var (tenant, _, tarifas) = EbroDeLaMaqueta();

        // El Excel «Exportar extranjeros del mes» lista 16 filas; la línea valoró 14 (p. ej. la vigencia solo cubría parte del mes).
        var empresa = Construir(Serie(0, tenant), Extranjeros((Ebro, 16)), tarifas).Empresas.Single();

        var extranjeros = empresa.Concepto(Ext);
        extranjeros.Unidades.Should().Be(14, "las unidades que se enseñan son las que se valoraron");
        extranjeros.Precio.Should().Be(40m);
        (extranjeros.Unidades * extranjeros.Precio).Should().Be(extranjeros.Importe);
        empresa.Extranjeros.Should().Be(14);
        empresa.GestionesExtranjero.Should().Be(14);
        empresa.ExtranjerosEnExcel.Should().Be(16, "el conteo del Excel queda como información complementaria");
        empresa.Concepto(Centro).Unidades.Should().Be(2);
        (empresa.Concepto(Centro).Unidades * empresa.Concepto(Centro).Precio).Should().Be(empresa.Concepto(Centro).Importe);
    }

    [Fact]
    public void Sin_linea_por_ejemplo_el_Coordinador_CAE_las_unidades_de_extranjeros_son_las_del_conteo_del_Excel()
    {
        var tenant = Tenant(Ebro, "Obras y Estructuras del Ebro S.L.", Cifras(2, 1, 212), facturacion: null);

        var empresa = Construir(Serie(0, tenant), Extranjeros((Ebro, 16)), tarifas: null).Empresas.Single();

        empresa.Extranjeros.Should().Be(16);
        empresa.CentrosNuevos.Should().Be(2);
        empresa.ExtranjerosEnExcel.Should().Be(16);
    }

    [Fact]
    public void Sin_tarifa_las_unidades_se_cuentan_y_no_hay_importe_ni_cero_euros()
    {
        var (_, extranjeros, _) = EbroDeLaMaqueta();
        var sinLineas = Tenant(Ebro, "Obras y Estructuras del Ebro S.L.", Cifras(2, 1, 212), []);
        List<TarifaOperadorCaeDto> deOtraEmpresa = [Tarifa(Reformas, Ext, 35m)];

        var empresa = Construir(Serie(0, sinLineas), extranjeros, deOtraEmpresa).Empresas.Single();

        empresa.SinTarifa.Should().BeTrue();
        empresa.SinTarifaEnElMes.Should().BeFalse();
        empresa.Cuota.Should().BeEmpty();
        empresa.Extranjeros.Should().Be(14);
        empresa.Conceptos.Should().OnlyContain(c => c.Estado == EstadoTarifaConcepto.SinTarifa && c.Importe == null);
        empresa.IngresoPorGestion.Should().BeNull();
    }

    [Fact]
    public void Un_Centro_con_el_cobro_desactivado_muestra_sus_unidades_y_no_se_cobra()
    {
        var tenant = Tenant(Ebro, "Obras y Estructuras del Ebro S.L.", Cifras(3, 0, 100),
            [Linea(Ext, 14, 560m), Linea(Centro, 3, 0m), Linea(Obra, 0, 0m)]);
        List<TarifaOperadorCaeDto> tarifas = [Tarifa(Ebro, Ext, 40m), Tarifa(Ebro, Centro, 25m, activa: false), Tarifa(Ebro, Obra, 120m)];

        var empresa = Construir(Serie(0, tenant), Extranjeros((Ebro, 14)), tarifas).Empresas.Single();

        empresa.CentrosNuevos.Should().Be(3);
        empresa.Concepto(Centro).Estado.Should().Be(EstadoTarifaConcepto.NoSeCobra);
        empresa.Concepto(Centro).Importe.Should().BeNull();
        empresa.Cuota.Should().Equal(new ImporteEnMoneda("EUR", 560m));
    }

    [Fact]
    public void Con_todos_los_conceptos_sin_cobrar_la_cuota_es_cero_euros_y_no_un_hueco()
    {
        var tenant = Tenant(Ebro, "Obras y Estructuras del Ebro S.L.", Cifras(3, 1, 100),
            [Linea(Ext, 14, 0m), Linea(Centro, 3, 0m), Linea(Obra, 1, 0m)]);
        List<TarifaOperadorCaeDto> tarifas =
            [Tarifa(Ebro, Ext, 40m, activa: false), Tarifa(Ebro, Centro, 25m, activa: false), Tarifa(Ebro, Obra, 120m, activa: false)];

        var modelo = Construir(Serie(0, tenant), Extranjeros((Ebro, 14)), tarifas);
        var empresa = modelo.Empresas.Single();

        empresa.Conceptos.Should().OnlyContain(c => c.Estado == EstadoTarifaConcepto.NoSeCobra);
        empresa.Cuota.Should().Equal(new ImporteEnMoneda("EUR", 0m));
        modelo.Total.Should().Equal(new ImporteEnMoneda("EUR", 0m));
    }

    [Fact]
    public void Sin_acceso_a_las_tarifas_solo_se_muestran_unidades()
    {
        var tenant = Tenant(Ebro, "Obras y Estructuras del Ebro S.L.", Cifras(2, 1, 212), facturacion: null);

        var modelo = Construir(Serie(0, tenant), Extranjeros((Ebro, 14)), tarifas: null);
        var empresa = modelo.Empresas.Single();

        modelo.ImportesOcultos.Should().BeTrue();
        modelo.PuedeEditarTarifas.Should().BeFalse();
        empresa.ImportesOcultos.Should().BeTrue();
        empresa.SinTarifa.Should().BeFalse("sin acceso no se sabe si hay tarifa: no se afirma que falte");
        empresa.Cuota.Should().BeEmpty();
        empresa.Extranjeros.Should().Be(14);
        modelo.Total.Should().BeEmpty();
    }

    [Fact]
    public void Una_tarifa_fijada_hoy_al_ver_un_mes_anterior_no_se_relee_y_el_mes_sale_sin_tarifa_en_ese_mes()
    {
        // La tarifa se fijó el 20-oct. Septiembre se valoró antes: la serie no devolvió líneas porque entonces no había versión en vigor.
        var septiembre = Tenant(Ebro, "Obras y Estructuras del Ebro S.L.", Cifras(2, 1, 212), [],
            estado: EstadoCifraMensual.Existe, anyo: 2026, mes: 9);
        List<TarifaOperadorCaeDto> tarifas =
            [Tarifa(Ebro, Ext, 40m, desde: Ahora), Tarifa(Ebro, Centro, 25m, desde: Ahora), Tarifa(Ebro, Obra, 120m, desde: Ahora)];

        var empresa = Construir(Serie(0, septiembre), Extranjeros((Ebro, 14)), tarifas, mes: 9).Empresas.Single();

        empresa.SinTarifaEnElMes.Should().BeTrue();
        empresa.SinTarifa.Should().BeFalse("la Empresa sí tiene tarifa, pero no valía en septiembre");
        empresa.Cuota.Should().BeEmpty();
        empresa.Conceptos.Should().OnlyContain(c => c.Estado == EstadoTarifaConcepto.SinTarifa && c.Precio == null && c.Importe == null);
        empresa.Extranjeros.Should().Be(14, "las unidades se cuentan igual");
    }

    [Fact]
    public void Un_cambio_de_EUR_a_USD_con_el_mes_sin_cerrar_enseña_la_moneda_y_el_precio_de_la_linea()
    {
        // Septiembre terminó y no tiene cierre: la serie lo valoró con la tarifa de ahora (USD, fijada ayer), no con la de fin de mes (EUR).
        var ayer = Ahora.AddDays(-1);
        var septiembre = Tenant(Ebro, "Obras y Estructuras del Ebro S.L.", Cifras(0, 0, 212),
            [Linea(Ext, 10, 120m, "USD")], estado: EstadoCifraMensual.Estimado, anyo: 2026, mes: 9);
        List<TarifaOperadorCaeDto> tarifas =
            [Tarifa(Ebro, Ext, 40m, moneda: "EUR"), Tarifa(Ebro, Ext, 12m, moneda: "USD", desde: ayer)];

        var empresa = Construir(Serie(0, septiembre), Extranjeros((Ebro, 10)), tarifas, mes: 9).Empresas.Single();

        var extranjeros = empresa.Concepto(Ext);
        extranjeros.Moneda.Should().Be("USD");
        extranjeros.Precio.Should().Be(12m);
        extranjeros.Importe.Should().Be(120m);
        empresa.Cuota.Should().Equal(new ImporteEnMoneda("USD", 120m));
    }

    [Fact]
    public void Un_mes_cerrado_en_euros_sigue_en_euros_aunque_hoy_la_tarifa_sea_en_dolares()
    {
        var septiembre = Tenant(Ebro, "Obras y Estructuras del Ebro S.L.", Cifras(0, 0, 212),
            [Linea(Ext, 10, 400m, "EUR")], estado: EstadoCifraMensual.Existe, anyo: 2026, mes: 9);
        List<TarifaOperadorCaeDto> tarifas =
            [Tarifa(Ebro, Ext, 40m, moneda: "EUR"), Tarifa(Ebro, Ext, 12m, moneda: "USD", desde: Ahora.AddDays(-1))];

        var empresa = Construir(Serie(0, septiembre), Extranjeros((Ebro, 10)), tarifas, mes: 9).Empresas.Single();

        empresa.Concepto(Ext).Moneda.Should().Be("EUR");
        empresa.Concepto(Ext).Precio.Should().Be(40m);
        empresa.Cuota.Should().Equal(new ImporteEnMoneda("EUR", 400m));
        empresa.Congelada.Should().BeTrue();
    }

    [Fact]
    public void Solo_salen_las_Empresas_que_el_Operador_opera_en_el_mes_y_el_total_no_suma_las_demas()
    {
        var (ebro, extranjeros, tarifasEbro) = EbroDeLaMaqueta();
        var ajena = Tenant(Ajena, "Otra organización S.L.", Cifras(1, 0, 10), [Linea(Ext, 3, 120m)]);
        List<TarifaOperadorCaeDto> tarifas = [.. tarifasEbro, Tarifa(Ajena, Ext, 40m)];

        var modelo = Construir(Serie(0, ebro, ajena), extranjeros, tarifas, operadas: [Ebro]);

        modelo.Empresas.Select(e => e.TenantId).Should().Equal(Ebro);
        modelo.Total.Should().Equal(new ImporteEnMoneda("EUR", 730m));
    }

    [Fact]
    public void Las_obras_que_la_medicion_no_lleva_salen_como_sin_dato_y_no_como_cero()
    {
        var tenant = Tenant(Ebro, "Obras y Estructuras del Ebro S.L.", Cifras(2, obras: null, gestiones: null),
            [Linea(Ext, 14, 560m), Linea(Centro, 2, 50m)]);
        List<TarifaOperadorCaeDto> tarifas = [Tarifa(Ebro, Ext, 40m), Tarifa(Ebro, Centro, 25m), Tarifa(Ebro, Obra, 120m)];

        var empresa = Construir(Serie(0, tenant), Extranjeros((Ebro, 14)), tarifas).Empresas.Single();

        empresa.ObrasNuevas.Should().BeNull();
        empresa.Concepto(Obra).Estado.Should().Be(EstadoTarifaConcepto.SinDato);
        empresa.Concepto(Obra).Importe.Should().BeNull();
        empresa.Gestiones.Should().BeNull();
        empresa.IngresoPorGestion.Should().BeNull();
    }

    [Fact]
    public void El_total_suma_por_moneda_las_empresas_sin_tarifa_no_suman_y_el_alcance_parcial_se_marca()
    {
        var (ebro, extranjeros, tarifasEbro) = EbroDeLaMaqueta();
        var reformas = Tenant(Reformas, "Reformas y Pavimentos Aragón S.L.", Cifras(1, 1, 140), [Linea(Ext, 6, 6m, "USD")]);
        var otra = Guid.Parse("e0000000-0000-0000-0000-000000000003");
        var sinTarifa = Tenant(otra, "Rubén Salas Quintana", Cifras(1, 0, 18), []);
        List<TarifaOperadorCaeDto> tarifas = [.. tarifasEbro, Tarifa(Reformas, Ext, 1m, moneda: "USD")];

        var modelo = Construir(Serie(2, ebro, reformas, sinTarifa), extranjeros, tarifas);

        modelo.Total.Should().Equal(new ImporteEnMoneda("EUR", 730m), new ImporteEnMoneda("USD", 6m));
        modelo.TenantsSinAlcanceCompleto.Should().Be(2);
        modelo.AlcanceParcial.Should().BeTrue("el total no incluye las Empresas que no se alcanzan por completo");
        modelo.Empresas.Single(e => e.TenantId == otra).SinTarifa.Should().BeTrue();
        Construir(Serie(0, ebro), extranjeros, tarifas).AlcanceParcial.Should().BeFalse();
    }

    [Fact]
    public void Un_mes_con_cierre_vigente_marca_la_tarifa_como_congelada()
    {
        var (tenant, extranjeros, tarifas) = EbroDeLaMaqueta();
        var cerrado = tenant with { Meses = [tenant.Meses[0] with { Estado = EstadoCifraMensual.Existe }] };

        var modelo = Construir(Serie(0, cerrado), extranjeros, tarifas);

        modelo.Estado.Should().Be(EstadoCifraMensual.Existe);
        modelo.Empresas.Single().Congelada.Should().BeTrue();
    }

    [Fact]
    public void El_estado_del_mes_es_el_del_total_de_la_serie_y_no_el_de_la_ultima_Empresa()
    {
        var (ebro, extranjeros, tarifasEbro) = EbroDeLaMaqueta();
        var reformas = Tenant(Reformas, "Reformas y Pavimentos Aragón S.L.", Cifras(1, 1, 140), [Linea(Ext, 6, 240m)],
            estado: EstadoCifraMensual.Estimado);
        List<TarifaOperadorCaeDto> tarifas = [.. tarifasEbro, Tarifa(Reformas, Ext, 40m)];

        var modelo = Construir(SerieConTotal(0, EstadoCifraMensual.Parcial, ebro, reformas), extranjeros, tarifas);

        modelo.Empresas.Last().Estado.Should().Be(EstadoCifraMensual.Estimado);
        modelo.Estado.Should().Be(EstadoCifraMensual.Parcial);
    }

    [Fact]
    public void Sin_unidades_en_la_linea_de_un_mes_en_curso_el_precio_es_el_de_la_version_con_la_que_se_valoro_ahora()
    {
        var tenant = Tenant(Ebro, "Obras y Estructuras del Ebro S.L.", Cifras(0, 0, 10),
            [Linea(Ext, 0, 0m), Linea(Centro, 0, 0m), Linea(Obra, 0, 0m)]);
        List<TarifaOperadorCaeDto> tarifas = [Tarifa(Ebro, Ext, 40m), Tarifa(Ebro, Centro, 25m), Tarifa(Ebro, Obra, 120m)];

        var empresa = Construir(Serie(0, tenant), Extranjeros(), tarifas).Empresas.Single();

        empresa.Concepto(Ext).Precio.Should().Be(40m);
        empresa.Concepto(Obra).Precio.Should().Be(120m);
        empresa.Cuota.Should().Equal(new ImporteEnMoneda("EUR", 0m));
    }

    [Fact]
    public void Sin_unidades_en_la_linea_de_un_mes_ya_valorado_no_se_inventa_el_precio_ni_se_relee_la_tarifa()
    {
        var septiembre = Tenant(Ebro, "Obras y Estructuras del Ebro S.L.", Cifras(0, 0, 10),
            [Linea(Ext, 0, 0m, "USD"), Linea(Centro, 0, 0m, "USD"), Linea(Obra, 0, 0m, "USD")],
            estado: EstadoCifraMensual.Existe, anyo: 2026, mes: 9);
        List<TarifaOperadorCaeDto> tarifas = [Tarifa(Ebro, Ext, 40m), Tarifa(Ebro, Centro, 25m), Tarifa(Ebro, Obra, 120m)];

        var empresa = Construir(Serie(0, septiembre), Extranjeros(), tarifas, mes: 9).Empresas.Single();

        empresa.Conceptos.Should().OnlyContain(c => c.Estado == EstadoTarifaConcepto.SinDato && c.Precio == null && c.Moneda == "USD");
        empresa.Cuota.Should().Equal(new ImporteEnMoneda("USD", 0m));
    }

    [Fact]
    public void Un_mes_no_valido_o_sin_empresas_no_da_filas()
    {
        FacturacionOperadorModelo.Construir(1900, 1, Serie(0), Extranjeros(), [], [], Ahora).Empresas.Should().BeEmpty();
        Construir(Serie(1), Extranjeros(), []).Empresas.Should().BeEmpty();
    }
}
