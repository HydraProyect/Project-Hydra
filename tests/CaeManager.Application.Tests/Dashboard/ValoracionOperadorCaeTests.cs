using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Dashboard;
using CaeManager.Domain.Documentos;
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

    // ------------------------------------------------ Vigencia de la Asignación de Operación en el mes

    private static readonly DateTime InicioOctubre = new(2026, 9, 30, 22, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime InicioNoviembre = new(2026, 10, 31, 23, 0, 0, DateTimeKind.Utc);
    private static DateTime Dia(int dia) => new(2026, 10, dia, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Sin_Asignacion_en_vigor_en_el_mes_no_hay_tramos()
    {
        ValoracionOperadorCae.TramosEnElMes([], InicioOctubre, InicioNoviembre).Should().BeEmpty();
        ValoracionOperadorCae.TramosEnElMes(
            [new VigenciaAsignacionOperacion(Enero, Marzo), new VigenciaAsignacionOperacion(InicioNoviembre, null)],
            InicioOctubre, InicioNoviembre).Should().BeEmpty("una cerrada antes del mes y otra que empieza al acabarlo no cubren nada");
    }

    [Fact]
    public void Una_Asignacion_abierta_desde_antes_cubre_el_mes_entero()
    {
        var tramos = ValoracionOperadorCae.TramosEnElMes([new VigenciaAsignacionOperacion(Enero, null)], InicioOctubre, InicioNoviembre);

        tramos.Should().Equal(new TramoDeVigencia(InicioOctubre, InicioNoviembre));
        ValoracionOperadorCae.CubreElMes(tramos, InicioOctubre, InicioNoviembre).Should().BeTrue();
    }

    [Fact]
    public void Una_Asignacion_que_empieza_o_termina_dentro_del_mes_se_recorta_y_no_cubre_el_mes()
    {
        var empieza = ValoracionOperadorCae.TramosEnElMes([new VigenciaAsignacionOperacion(Dia(10), null)], InicioOctubre, InicioNoviembre);
        var termina = ValoracionOperadorCae.TramosEnElMes([new VigenciaAsignacionOperacion(Enero, Dia(20))], InicioOctubre, InicioNoviembre);

        empieza.Should().Equal(new TramoDeVigencia(Dia(10), InicioNoviembre));
        termina.Should().Equal(new TramoDeVigencia(InicioOctubre, Dia(20)));
        ValoracionOperadorCae.CubreElMes(empieza, InicioOctubre, InicioNoviembre).Should().BeFalse();
        ValoracionOperadorCae.CubreElMes(termina, InicioOctubre, InicioNoviembre).Should().BeFalse();
    }

    [Fact]
    public void Las_vigencias_que_se_solapan_o_se_tocan_se_unen_y_las_separadas_no()
    {
        var tramos = ValoracionOperadorCae.TramosEnElMes(
        [
            new VigenciaAsignacionOperacion(Dia(15), Dia(25)),
            new VigenciaAsignacionOperacion(Dia(2), Dia(5)),
            new VigenciaAsignacionOperacion(Dia(5), Dia(8)),
            new VigenciaAsignacionOperacion(Dia(20), Dia(22)),
        ], InicioOctubre, InicioNoviembre);

        tramos.Should().Equal(new TramoDeVigencia(Dia(2), Dia(8)), new TramoDeVigencia(Dia(15), Dia(25)));
    }

    [Fact]
    public void Acotar_sustituye_solo_las_unidades_del_Operador_y_la_valoracion_usa_las_acotadas()
    {
        var acotada = ValoracionOperadorCae.AcotadaA(Medicion(altasCentros: 6, visitasExtranjero: 9, obras: 4), new UnidadesOperadorCae(1, 2, 3));

        acotada.AltasCentros.Should().Be(1);
        acotada.ObrasNuevas.Should().Be(2);
        acotada.VisitasTrabajadorExtranjero.Should().Be(3);
        acotada.TrabajadoresActivos.Should().Be(10, "las demás cifras del Cuadro no se tocan");
        ValoracionOperadorCae.Valorar(acotada, Empresa, [Tarifa(ConceptoTarifaOperadorCae.CentroNuevo, 50m, Enero)], Marzo)
            .Single().Importe.Should().Be(50m);
    }

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
    public void El_cobro_desactivado_sale_con_sus_unidades_e_importe_cero_porque_las_unidades_se_muestran_siempre()
    {
        var medicion = Medicion();
        var lineas = ValoracionOperadorCae.Valorar(medicion, Empresa,
            [Tarifa(ConceptoTarifaOperadorCae.CentroNuevo, 50m, Enero, activa: false)], Marzo);

        lineas.Should().ContainSingle()
            .Which.Should().Match<LineaFacturacionCuadroDto>(l =>
                l.Concepto == ConceptoTarifaOperadorCae.CentroNuevo && l.Unidades == 2 && l.Importe == 0m);
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

    /// <summary>
    /// El cumplimiento de contexto Empresa (pares exigidos) se suma por pares, nunca como media de porcentajes; si un Tenant
    /// no lo trae, el total no se inventa.
    /// </summary>
    [Fact]
    public void El_cumplimiento_de_pares_exigidos_suma_pares_y_sin_dato_de_un_Tenant_queda_a_null()
    {
        var a = Medicion() with { CumplimientoExigido = new FraccionCumplimiento(25, 100) };
        var b = Medicion() with { CumplimientoExigido = new FraccionCumplimiento(9, 10) };

        ObtenerCuadroDireccionQueryHandler.Cifras([a, b], EstadoCifraMensual.Parcial, Marzo).Cumplimiento.Exigido!.Porcentaje
            .Should().Be(31, "34 de 110 pares, no la media de 25 % y 90 %");
        ObtenerCuadroDireccionQueryHandler.Cifras([a, Medicion()], EstadoCifraMensual.Parcial, Marzo).Cumplimiento.Exigido
            .Should().BeNull();
    }

    [Fact]
    public void El_cumplimiento_exigido_del_Tenant_solo_cuenta_los_pares_de_sus_Empresas_propias_como_el_listado_de_Empresas()
    {
        var propia = Guid.NewGuid();
        var subcontrata = Guid.NewGuid();
        var centro = Guid.NewGuid();
        ParDocumentalExigido Par(Guid? empresa, EstadoDocumento estado) =>
            new(centro, Guid.NewGuid(), empresa, Guid.NewGuid(), Guid.NewGuid(), estado, PendienteEnPlataforma: false);

        var pares = new[]
        {
            Par(propia, EstadoDocumento.Vigente),
            Par(propia, EstadoDocumento.Faltante),
            Par(propia, EstadoDocumento.Vencido),
            Par(propia, EstadoDocumento.Faltante),
        }.Concat(Enumerable.Range(0, 20).Select(_ => Par(subcontrata, EstadoDocumento.Vigente)))
         .Append(Par(null, EstadoDocumento.Vigente));

        var fraccion = ObtenerCumplimientoExigidoTenantQueryHandler.DeEmpresasPropias(pares, [propia]);

        fraccion.Should().Be(new FraccionCumplimiento(1, 4),
            "la subcontrata que comparte Centro y el Trabajador sin Empresa no entran: /empresas diría 25 %");
        ObtenerCumplimientoExigidoTenantQueryHandler.DeEmpresasPropias(pares, []).Requeridos.Should().Be(0);
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
