using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Dashboard;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Dashboard;

/// <summary>Lo que el Operador CAE externo cobra a una Empresa gestionada (decisión de Chris, 2026-10-20).</summary>
public class ValoracionOperadorCaeTests
{
    private static readonly Guid Empresa = Guid.NewGuid();
    private static readonly DateTime Enero = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Marzo = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static MedicionCuadroDireccion Medicion(int altasCentros = 2, int? visitasExtranjero = 5, int? obras = 1) =>
        new(10, 0, 0, 3, altasCentros, 0, 0, [], new KpisDashboardDto(0, 0, 0, 0, 0, 0, 0, 0),
            ExtranjerosActivos: 2, ObrasNuevas: obras, VisitasTrabajadorExtranjero: visitasExtranjero);

    [Fact]
    public void La_visita_de_Trabajador_extranjero_cuenta_Trabajador_por_Visita_y_no_las_personas_activas()
    {
        var medicion = Medicion(visitasExtranjero: 3) with { ExtranjerosActivos = 1 };

        ValoracionOperadorCae.UnidadesDe(medicion, ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero).Should().Be(3,
            "un Trabajador extranjero recurrente en tres Visitas del mes cuenta tres (decisión de Chris, 2026-10-20)");
        ValoracionOperadorCae.UnidadesDe(Medicion(visitasExtranjero: null), ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero)
            .Should().BeNull("una foto de cierre anterior al campo no se valora ni se reconstruye");
    }

    [Fact]
    public void Un_mes_cerrado_antes_de_existir_tarifas_sale_sin_importe()
    {
        ValoracionOperadorCae.Valorar(Medicion(), Empresa,
            [Tarifa(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 20m, Marzo)], Enero).Should().BeEmpty();
    }

    private static TarifaOperadorCaeDto Tarifa(ConceptoTarifaOperadorCae concepto, decimal precio, DateTime desde, bool activa = true, Guid? empresa = null) =>
        new(empresa ?? Empresa, concepto, precio, "EUR", activa, desde);

    [Fact]
    public void Valora_cada_concepto_con_tarifa_activa_por_sus_unidades()
    {
        var lineas = ValoracionOperadorCae.Valorar(Medicion(), Empresa,
        [
            Tarifa(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 20m, Enero),
            Tarifa(ConceptoTarifaOperadorCae.CentroNuevo, 50m, Enero),
            Tarifa(ConceptoTarifaOperadorCae.ObraNueva, 100m, Enero),
        ], Marzo);

        lineas.Should().HaveCount(3);
        lineas.Sum(l => l.Importe).Should().Be(5 * 20m + 2 * 50m + 1 * 100m);
    }

    [Fact]
    public void El_cobro_desactivado_no_se_valora_pero_las_unidades_siguen_en_la_medicion()
    {
        var medicion = Medicion();
        var lineas = ValoracionOperadorCae.Valorar(medicion, Empresa,
            [Tarifa(ConceptoTarifaOperadorCae.CentroNuevo, 50m, Enero, activa: false)], Marzo);

        lineas.Should().BeEmpty();
        ValoracionOperadorCae.UnidadesDe(medicion, ConceptoTarifaOperadorCae.CentroNuevo).Should().Be(2);
    }

    [Fact]
    public void Un_mes_cerrado_se_valora_con_la_version_vigente_en_su_cierre_y_un_cambio_posterior_no_lo_altera()
    {
        var versiones = new[]
        {
            Tarifa(ConceptoTarifaOperadorCae.CentroNuevo, 50m, Enero),
            Tarifa(ConceptoTarifaOperadorCae.CentroNuevo, 80m, Marzo),
        };

        ValoracionOperadorCae.Valorar(Medicion(), Empresa, versiones, Marzo.AddDays(-1)).Single().Importe.Should().Be(100m);
        ValoracionOperadorCae.Valorar(Medicion(), Empresa, versiones, Marzo).Single().Importe.Should().Be(160m);
    }

    [Fact]
    public void Sin_tarifa_de_esa_Empresa_o_sin_unidades_en_la_foto_no_hay_linea()
    {
        ValoracionOperadorCae.Valorar(Medicion(), Empresa,
            [Tarifa(ConceptoTarifaOperadorCae.CentroNuevo, 50m, Enero, empresa: Guid.NewGuid())], Marzo).Should().BeEmpty();

        ValoracionOperadorCae.Valorar(Medicion(obras: null), Empresa,
            [Tarifa(ConceptoTarifaOperadorCae.ObraNueva, 100m, Enero)], Marzo).Should().BeEmpty("una foto anterior no lleva Obras nuevas");

        ValoracionOperadorCae.Valorar(Medicion(), Empresa,
            [Tarifa(ConceptoTarifaOperadorCae.CentroNuevo, 50m, Marzo)], Enero).Should().BeEmpty("la tarifa todavía no existía");
    }

    [Fact]
    public void Las_Obras_nuevas_y_los_Centros_nuevos_por_Cliente_suman_en_el_total()
    {
        var tenant = Guid.NewGuid();
        var cliente = Guid.NewGuid();
        var a = Medicion(obras: 2) with { CentrosNuevosPorCliente = [new(tenant, cliente, "Cliente A", 2)] };
        var b = Medicion(obras: 1) with { CentrosNuevosPorCliente = [new(tenant, cliente, "Cliente A", 1), new(tenant, Guid.NewGuid(), "Cliente B", 4)] };

        var cifras = ObtenerCuadroDireccionQueryHandler.Cifras([a, b], EstadoCifraMensual.Parcial, Marzo);

        cifras.ObrasNuevas!.Valor.Should().Be(3);
        cifras.CentrosNuevosPorCliente!.Select(c => (c.ClienteNombre, c.Centros)).Should().Equal(("Cliente B", 4), ("Cliente A", 3));
    }

    [Fact]
    public void Una_foto_sin_los_campos_nuevos_deja_las_cifras_a_null()
    {
        var cifras = ObtenerCuadroDireccionQueryHandler.Cifras([Medicion(obras: null)], EstadoCifraMensual.Existe, Marzo);

        cifras.ObrasNuevas.Should().BeNull();
        cifras.CentrosNuevosPorCliente.Should().BeNull();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1.234)]
    public void La_tarifa_rechaza_precios_negativos_o_con_mas_de_dos_decimales(decimal precio)
    {
        var accion = () => TarifaOperadorCae.Registrar(Empresa, ConceptoTarifaOperadorCae.CentroNuevo, precio, "EUR", true, Guid.NewGuid(), Marzo);
        accion.Should().Throw<ArgumentException>();
    }
}
