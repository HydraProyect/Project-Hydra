using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Dashboard;

/// <summary>La fusión pura del Cuadro de dirección, con mediciones fabricadas por Tenant beneficiario.</summary>
public class ObtenerCuadroDireccionQueryHandlerTests
{
    private static readonly MesCuadroDireccion Octubre = new(2026, 10);
    private static readonly DateTime EnOctubre = new(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EnNoviembre = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);

    private static ClienteAutorizadoDto Tenant(string nombre) => new(Guid.NewGuid(), nombre, EsOrigen: false);

    private static MedicionCuadroDireccion Medicion(
        int activos = 0, int altas = 0, int bajas = 0, int centros = 0, int altasCentros = 0,
        int documentos = 0, int requerimientos = 0, IReadOnlyList<DateTime>? solicitudes = null,
        int vigentes = 0, int vencidos = 0, int sinConfirmar = 0, int paresExigidos = 0, int? extranjeros = null) =>
        new(activos, altas, bajas, centros, altasCentros, documentos, requerimientos, solicitudes ?? [],
            new KpisDashboardDto(activos, centros, vencidos, 0, 0, vigentes, 0, 0,
                DocumentosSinConfirmar: sinConfirmar, ParesExigidosSinDocumento: paresExigidos),
            ExtranjerosActivos: extranjeros);

    private static CuadroDireccionDto Fusionar(
        DateTime ahora, params (ClienteAutorizadoDto Cliente, MedicionCuadroDireccion Medicion)[] porTenant) =>
        ObtenerCuadroDireccionQueryHandler.Fusionar(Octubre, ahora, porTenant, new Dictionary<Guid, PrincipalDeCarteraDto>(), 0);

    [Fact]
    public void Suma_los_recuentos_de_todos_los_Tenants_beneficiarios()
    {
        var resultado = Fusionar(EnOctubre,
            (Tenant("Ibertec"), Medicion(activos: 100, altas: 4, bajas: 2, centros: 10, altasCentros: 1, documentos: 80, requerimientos: 12)),
            (Tenant("EcoPlant"), Medicion(activos: 50, altas: 1, bajas: 3, centros: 4, altasCentros: 2, documentos: 20, requerimientos: 3)));

        resultado.Total.TrabajadoresActivos.Valor.Should().Be(150);
        resultado.Total.AltasTrabajadores.Valor.Should().Be(5);
        resultado.Total.BajasTrabajadores.Valor.Should().Be(5);
        resultado.Total.CentrosActivos.Valor.Should().Be(14);
        resultado.Total.AltasCentros.Valor.Should().Be(3);
        resultado.Total.DocumentosGestionados.Valor.Should().Be(100);
        resultado.Total.Requerimientos.Valor.Should().Be(15);
        resultado.Tenants.Should().HaveCount(2);
    }

    [Fact]
    public void Cada_Tenant_conserva_sus_propias_cifras_y_salen_ordenados_por_nombre()
    {
        var ibertec = Tenant("Ibertec");
        var ecoPlant = Tenant("EcoPlant");

        var resultado = Fusionar(EnOctubre, (ibertec, Medicion(activos: 100)), (ecoPlant, Medicion(activos: 50)));

        resultado.Tenants.Select(t => t.TenantNombre).Should().Equal("EcoPlant", "Ibertec");
        resultado.Tenants.Single(t => t.TenantId == ibertec.TenantId).Cifras.TrabajadoresActivos.Valor.Should().Be(100);
        resultado.Tenants.Single(t => t.TenantId == ecoPlant.TenantId).Cifras.TrabajadoresActivos.Valor.Should().Be(50);
    }

    [Fact]
    public void En_el_mes_en_curso_cada_cifra_es_parcial()
    {
        var resultado = Fusionar(EnOctubre, (Tenant("Ibertec"), Medicion(activos: 3, solicitudes: [EnOctubre])));

        resultado.Estado.Should().Be(EstadoCifraMensual.Parcial);
        resultado.CorteUtc.Should().Be(EnOctubre);
        var cifras = new[] { resultado.Total, resultado.Tenants[0].Cifras };
        foreach (var c in cifras)
        {
            new[]
            {
                c.TrabajadoresActivos, c.AltasTrabajadores, c.BajasTrabajadores, c.CentrosActivos, c.AltasCentros,
                c.DocumentosGestionados, c.Requerimientos, c.Solicitudes.Total,
            }.Should().OnlyContain(x => x.Estado == EstadoCifraMensual.Parcial);
        }
    }

    [Fact]
    public void Un_mes_terminado_sin_cierre_sale_estimado_con_corte_al_final_del_mes()
    {
        var resultado = Fusionar(EnNoviembre, (Tenant("Ibertec"), Medicion(activos: 3)));

        resultado.Estado.Should().Be(EstadoCifraMensual.Estimado);
        resultado.CorteUtc.Should().Be(Octubre.FinUtc);
        resultado.Total.TrabajadoresActivos.Estado.Should().Be(EstadoCifraMensual.Estimado);
    }

    [Fact]
    public void El_cumplimiento_del_total_se_recalcula_sobre_los_recuentos_sumados_y_no_es_la_media_de_porcentajes()
    {
        // Ibertec: 1 de 1 al día (100 %). EcoPlant: 0 de 99 (0 %). Media simple: 50 %; sobre la suma: 1 %.
        var resultado = Fusionar(EnOctubre,
            (Tenant("Ibertec"), Medicion(vigentes: 1)),
            (Tenant("EcoPlant"), Medicion(vencidos: 99)));

        resultado.Total.Cumplimiento.Porcentaje.Should().Be(
            KpisDashboardDto.FraccionDe(1, 0, 0, 99, 0, 0).Porcentaje);
        resultado.Total.Cumplimiento.Porcentaje.Should().Be(1);
        resultado.Total.Cumplimiento.SinDatos.Should().BeFalse();
        resultado.Total.Cumplimiento.MedidoEnUtc.Should().Be(EnOctubre);
    }

    [Fact]
    public void Sin_documentos_y_con_pares_exigidos_el_cumplimiento_es_cero_y_sin_nada_que_medir_no_se_pinta()
    {
        var conExigidos = Fusionar(EnOctubre, (Tenant("Ibertec"), Medicion(paresExigidos: 4)));
        conExigidos.Total.Cumplimiento.Porcentaje.Should().Be(0);
        conExigidos.Total.Cumplimiento.SinDatos.Should().BeFalse();

        var sinNada = Fusionar(EnOctubre, (Tenant("Ibertec"), Medicion()));
        sinNada.Total.Cumplimiento.SinDatos.Should().BeTrue();
    }

    [Fact]
    public void Las_solicitudes_se_reparten_por_franja_sumando_las_de_todos_los_Tenants()
    {
        var lunes0930 = new DateTime(2026, 10, 5, 7, 30, 0, DateTimeKind.Utc);
        var lunes0945 = new DateTime(2026, 10, 12, 7, 45, 0, DateTimeKind.Utc);

        var resultado = Fusionar(EnOctubre,
            (Tenant("Ibertec"), Medicion(solicitudes: [lunes0930])),
            (Tenant("EcoPlant"), Medicion(solicitudes: [lunes0945])));

        resultado.Total.Solicitudes.Total.Valor.Should().Be(2);
        resultado.Total.Solicitudes.PorFranja.Should().Equal(new FranjaSolicitudesDto(DayOfWeek.Monday, 9, 2));
        resultado.Tenants.Should().OnlyContain(t => t.Cifras.Solicitudes.Total.Valor == 1);
    }

    [Fact]
    public void El_principal_de_cartera_se_asocia_a_su_Tenant_y_el_resto_queda_sin_principal()
    {
        var ibertec = Tenant("Ibertec");
        var ecoPlant = Tenant("EcoPlant");
        var principal = new PrincipalDeCarteraDto(Guid.NewGuid(), "Gestora de prueba", "GestorCae");

        var resultado = ObtenerCuadroDireccionQueryHandler.Fusionar(
            Octubre, EnOctubre, [(ibertec, Medicion()), (ecoPlant, Medicion())],
            new Dictionary<Guid, PrincipalDeCarteraDto> { [ibertec.TenantId] = principal }, 0);

        resultado.Tenants.Single(t => t.TenantId == ibertec.TenantId).PrincipalDeCartera.Should().Be(principal);
        resultado.Tenants.Single(t => t.TenantId == ecoPlant.TenantId).PrincipalDeCartera.Should().BeNull();
    }

    [Fact]
    public void Los_Tenants_sin_alcance_completo_se_cuentan_y_no_aportan_al_total()
    {
        var resultado = ObtenerCuadroDireccionQueryHandler.Fusionar(
            Octubre, EnOctubre, [(Tenant("Ibertec"), Medicion(activos: 7))], new Dictionary<Guid, PrincipalDeCarteraDto>(), 2);

        resultado.TenantsSinAlcanceCompleto.Should().Be(2);
        resultado.Total.TrabajadoresActivos.Valor.Should().Be(7);
    }

    [Theory]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    [InlineData(1999, 10)]
    [InlineData(9999, 12)]
    [InlineData(int.MinValue, 1)]
    public void Un_mes_no_valido_no_se_puede_calcular(int anyo, int mes) =>
        MesCuadroDireccion.EsValido(anyo, mes).Should().BeFalse();

    [Theory]
    [InlineData(2000, 1)]
    [InlineData(2026, 10)]
    [InlineData(9998, 12)]
    public void Un_mes_valido_tiene_fechas_calculables(int anyo, int mes)
    {
        MesCuadroDireccion.EsValido(anyo, mes).Should().BeTrue();
        var calculable = new MesCuadroDireccion(anyo, mes);
        calculable.FinUtc.Should().BeAfter(calculable.InicioUtc);
    }

    [Fact]
    public void Un_mes_sin_cifras_sale_vacio_con_su_motivo_y_sin_fechas_si_no_es_valido()
    {
        var resultado = ObtenerCuadroDireccionQueryHandler.SinCifras(2026, 13, null, EnOctubre, CuadroDireccionSinCifras.MesNoValido);

        resultado.SinCifras.Should().Be(CuadroDireccionSinCifras.MesNoValido);
        resultado.Anyo.Should().Be(2026);
        resultado.Mes.Should().Be(13);
        resultado.InicioUtc.Should().Be(DateTime.MinValue);
        resultado.Tenants.Should().BeEmpty();
        resultado.Total.TrabajadoresActivos.Valor.Should().Be(0);
        resultado.Total.Cumplimiento.SinDatos.Should().BeTrue();
    }

    [Fact]
    public void Un_mes_futuro_sale_vacio_con_sus_fechas()
    {
        var noviembre = new MesCuadroDireccion(2026, 11);

        var resultado = ObtenerCuadroDireccionQueryHandler.SinCifras(2026, 11, noviembre, EnOctubre, CuadroDireccionSinCifras.MesFuturo);

        resultado.SinCifras.Should().Be(CuadroDireccionSinCifras.MesFuturo);
        resultado.InicioUtc.Should().Be(noviembre.InicioUtc);
        resultado.FinUtc.Should().Be(noviembre.FinUtc);
        resultado.Tenants.Should().BeEmpty();
    }

    [Fact]
    public void Un_cuadro_calculado_no_lleva_motivo_sin_cifras() =>
        Fusionar(EnOctubre).SinCifras.Should().BeNull();

    [Fact]
    public void Sin_Tenants_el_total_es_cero_y_no_falla()
    {
        var resultado = Fusionar(EnOctubre);

        resultado.Tenants.Should().BeEmpty();
        resultado.Total.TrabajadoresActivos.Valor.Should().Be(0);
        resultado.Total.Solicitudes.PorFranja.Should().BeEmpty();
        resultado.Total.Cumplimiento.SinDatos.Should().BeTrue();
    }

    [Fact]
    public void Los_Trabajadores_extranjeros_se_suman_por_Tenant_y_en_el_total()
    {
        var ibertec = Tenant("Ibertec");
        var resultado = Fusionar(EnOctubre,
            (ibertec, Medicion(activos: 100, extranjeros: 12)),
            (Tenant("EcoPlant"), Medicion(activos: 50, extranjeros: 0)));

        resultado.Total.Extranjeros!.Valor.Should().Be(12);
        resultado.Tenants.Single(t => t.TenantId == ibertec.TenantId).Cifras.Extranjeros!.Valor.Should().Be(12);
        resultado.Tenants.Single(t => t.TenantNombre == "EcoPlant").Cifras.Extranjeros!.Valor.Should().Be(0, "un cero medido es un dato");
        resultado.Total.Extranjeros!.Estado.Should().Be(EstadoCifraMensual.Parcial);
    }

    [Fact]
    public void Si_algun_Tenant_no_midio_los_extranjeros_el_total_no_los_inventa()
    {
        var resultado = Fusionar(EnOctubre,
            (Tenant("Ibertec"), Medicion(activos: 100, extranjeros: 12)),
            (Tenant("EcoPlant"), Medicion(activos: 50)));

        resultado.Total.Extranjeros.Should().BeNull("un cierre anterior al campo no lo guardó y no se reconstruye");
        resultado.Tenants.Single(t => t.TenantNombre == "EcoPlant").Cifras.Extranjeros.Should().BeNull();
    }
}
