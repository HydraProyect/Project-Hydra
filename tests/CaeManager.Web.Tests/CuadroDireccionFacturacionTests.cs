using Bunit;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Common;
using CaeManager.Domain.Dashboard;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;
using CuadroDireccionPagina = CaeManager.Web.Features.CuadroDireccion.Pages.CuadroDireccion;

namespace CaeManager.Web.Tests;

/// <summary>
/// La pestaña Facturación del Cuadro de dirección contra su maqueta («Cuadro Facturacion TALVEG»): lo que el Operador CAE
/// externo cobra a cada Empresa gestionada, con la cifra de extranjeros del conteo que también usa el Excel y sin ningún
/// precio escrito en la pantalla.
/// </summary>
public partial class CuadroDireccionTests
{
    private static readonly Guid ClientePapelera = Guid.NewGuid();

    private static LineaFacturacionCuadroDto LineaTarifa(ConceptoTarifaOperadorCae c, int unidades, decimal precio, bool activo = true) =>
        new(c, c.ToString(), "EUR", unidades, activo ? unidades * precio : 0m, precio, activo);

    private static FacturacionOperadorCaeDto Facturacion(bool puedeFijar = true)
    {
        var hoy = DiaDeNegocio.Hoy();
        var conTarifa = CalculosFacturacionOperadorCae.Fila(
            TenantA, "Refrielectric S.A.", extranjeros: 12, centrosNuevos: 2, obrasNuevas: 1, gestiones: 100,
            [new CentrosNuevosPorClienteDto(TenantA, ClientePapelera, "Papelera del Jalón S.A.", 2)],
            puedeFijar
                ? [LineaTarifa(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 14, 40m), LineaTarifa(ConceptoTarifaOperadorCae.CentroNuevo, 2, 25m), LineaTarifa(ConceptoTarifaOperadorCae.ObraNueva, 1, 120m)]
                : null,
            puedeFijar ? [new TarifaVigenteOperadorCaeDto(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 40m, "EUR", true)] : []);
        var sinTarifa = CalculosFacturacionOperadorCae.Fila(TenantB, "Montajes Ebro S.L.", 3, 0, 0, 30, [], puedeFijar ? [] : null, []);
        var filas = new[] { conTarifa, sinTarifa };
        return new FacturacionOperadorCaeDto(
            hoy.Year, hoy.Month, puedeFijar, filas, CalculosFacturacionOperadorCae.Total(filas),
            [new CentrosNuevosPorClienteTotalDto("Papelera del Jalón S.A.", 2)], 0);
    }

    private IRenderedComponent<CuadroDireccionPagina> PintarFacturacion(
        FacturacionOperadorCaeDto facturacion, string rol, out MediatorFalso mediador, ExtranjerosDelMesDto? extranjeros = null)
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), rol, out mediador, facturacion: facturacion, extranjeros: extranjeros);
        IrA(cut, "Facturación");
        cut.WaitForAssertion(() => cut.Find("[data-facturacion=operador] [data-fact-kpi=cuota]"));
        return cut;
    }

    [Fact]
    public void El_KPI_de_Extranjeros_de_la_Vision_general_sale_del_conteo_del_Excel_y_no_de_la_facturacion_estimada()
    {
        var hoy = DiaDeNegocio.Hoy();
        var conteo = new ConteoExtranjerosCobrablesDelMesDto(
            hoy.Year, hoy.Month,
            [new ConteoExtranjerosCobrablesTenantDto(TenantA, "Refrielectric S.A.", 7, 5), new ConteoExtranjerosCobrablesTenantDto(TenantB, "Montajes Ebro S.L.", 3, 3)], 0);

        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _, conteo: conteo);

        cut.Find("[data-kpi=extranjeros] .cuadro-kpi-valor").TextContent.Trim().Should().Be("10");
    }

    [Fact]
    public void Sin_extranjeros_en_el_conteo_el_KPI_dice_cero()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);

        cut.Find("[data-kpi=extranjeros] .cuadro-kpi-valor").TextContent.Trim().Should().Be("0");
    }

    [Fact]
    public void La_pestana_Facturacion_pinta_cinco_KPI_y_la_cuota_por_Empresa_gestionada()
    {
        var cut = PintarFacturacion(Facturacion(), Roles.DireccionCae, out _);

        cut.FindAll("[data-facturacion=operador] .fact-kpi").Select(k => k.GetAttribute("data-fact-kpi"))
            .Should().Equal("cuota", "extranjeros", "centros", "obras", "ingreso");
        cut.Find("[data-fact-kpi=cuota] b").TextContent.Should().Contain("650");
        cut.Find("[data-fact-kpi=extranjeros] b").TextContent.Trim().Should().Be("15", "12 + 3: las asignaciones del conteo de las dos Empresas");

        var fila = cut.Find($"[data-tabla=facturacion-empresas] tr[data-empresa='{TenantA}']");
        fila.TextContent.Should().Contain("12").And.Contain("480").And.Contain("650");
        fila.TextContent.Should().Contain("Cartera de Marta Gestora");
    }

    [Fact]
    public void Una_Empresa_sin_tarifa_cuenta_sus_unidades_pero_no_enseña_ni_cero_euros()
    {
        var cut = PintarFacturacion(Facturacion(), Roles.DireccionCae, out _);

        var fila = cut.Find($"[data-tabla=facturacion-empresas] tr[data-empresa='{TenantB}']");
        fila.TextContent.Should().Contain("Sin tarifa").And.Contain("Fijar tarifa").And.Contain("3");
        fila.TextContent.Should().NotContain("0 €");
    }

    [Fact]
    public void Los_Centros_nuevos_se_desglosan_por_Cliente_empresarial_y_el_calculo_se_explica()
    {
        var cut = PintarFacturacion(Facturacion(), Roles.DireccionCae, out _);

        cut.Find("[data-fact=crecimiento]").TextContent.Should().Contain("Papelera del Jalón S.A.").And.Contain("El Cliente empresarial nuevo no se cobra");
        cut.Find("[data-fact=como-se-calcula]").TextContent.Should().Contain("nunca por persona");
    }

    [Fact]
    public void Quien_no_gestiona_las_tarifas_ve_las_unidades_pero_ni_importes_ni_botones_de_tarifas()
    {
        var cut = PintarFacturacion(Facturacion(puedeFijar: false), Roles.CoordinadorCae, out _);

        cut.Find("[data-fact-kpi=cuota] b").TextContent.Trim().Should().Be("—");
        cut.FindAll("[data-facturacion=operador] .fact-tarifas").Should().BeEmpty();
        cut.FindAll("[data-facturacion=operador] .fact-enlace-peq").Should().BeEmpty("sin permiso no hay «Fijar tarifa»");
        cut.Find($"[data-tabla=facturacion-empresas] tr[data-empresa='{TenantA}']").TextContent.Should().Contain("12");
    }

    [Fact]
    public void El_detalle_de_una_Empresa_lista_sus_extranjeros_y_exporta_solo_los_suyos()
    {
        var hoy = DiaDeNegocio.Hoy();
        var extranjeros = new ExtranjerosDelMesDto(
            hoy.Year, hoy.Month,
            [
                new FilaExtranjeroDelMesDto(TenantA, "Refrielectric S.A.", new DateOnly(hoy.Year, hoy.Month, 2), "Lucía Ferrer", "Refrielectric S.A.", null, "Papelera del Jalón S.A.", "Fábrica Calatayud",
                    new DateOnly(hoy.Year, hoy.Month, 3), new DateOnly(hoy.Year, hoy.Month, 6), "Mihai Popescu", "Pasaporte RO4400120", "Marta Gestora", Guid.NewGuid()),
                new FilaExtranjeroDelMesDto(TenantB, "Montajes Ebro S.L.", new DateOnly(hoy.Year, hoy.Month, 2), null, "Montajes Ebro S.L.", null, "Berriz", "Taller Berriz",
                    new DateOnly(hoy.Year, hoy.Month, 3), new DateOnly(hoy.Year, hoy.Month, 6), "Otra Persona", "Pasaporte MA1", null, Guid.NewGuid()),
            ], 0);
        var cut = PintarFacturacion(Facturacion(), Roles.DireccionCae, out _, extranjeros);

        cut.Find($"[data-tabla=facturacion-empresas] tr[data-empresa='{TenantA}'] .fact-enlace").Click();

        cut.WaitForAssertion(() => cut.Find("[data-tabla=extranjeros-empresa]"));
        var texto = cut.Find("[data-tabla=extranjeros-empresa]").TextContent;
        texto.Should().Contain("Mihai Popescu").And.Contain("Visita con Lucía Ferrer").And.NotContain("Otra Persona");
        cut.Find("[data-facturacion=operador] a[download]").GetAttribute("href").Should().Contain($"tenantId={TenantA}");
    }

    [Fact]
    public void Tarifas_abre_el_drawer_con_la_tarifa_vigente_y_Guardar_fija_solo_lo_que_cambio()
    {
        var cut = PintarFacturacion(Facturacion(), Roles.DireccionCae, out var mediador);

        cut.Find($"[data-tabla=facturacion-empresas] tr[data-empresa='{TenantA}'] .fact-tarifas").Click();

        var precio = cut.WaitForElement("[data-concepto=Extranjero] input[type=number]");
        precio.GetAttribute("value").Should().Be("40");
        precio.Change("45");
        cut.FindAll("button").First(b => b.TextContent.Contains("Guardar tarifas")).Click();

        cut.WaitForAssertion(() => mediador.TarifasFijadas.Should().NotBeEmpty());
        var fijadas = mediador.TarifasFijadas;
        fijadas.Select(c => (c.Concepto, c.PrecioUnitario, c.Activa)).Should().Contain((ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 45m, true));
        fijadas.Should().OnlyContain(c => c.PropietarioTenantId == TenantA && c.MonedaIso == "EUR");
    }
}
