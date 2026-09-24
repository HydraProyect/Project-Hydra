using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Dashboard;

/// <summary>
/// Defecto 2 de la fuga de alcance BPO (hallazgo Codex 2026-09-11): la media
/// de SLA documental de <c>ObtenerKpisGlobalesQuery</c> contaba al 100% —
/// "está al día"— cada Cliente Delegante donde el usuario no tiene ninguna
/// Asignación de Cartera, cuando ese 100% en realidad significa "no hay nada
/// que este usuario pueda evaluar ahí". Ver también
/// <c>ObtenerDashboardEjecutivoQueryHandlerTests</c>, mismo patrón de
/// <c>Fusionar</c> puro para su query hermana.
/// </summary>
public class ObtenerKpisGlobalesQueryHandlerTests
{
    private static ClienteAutorizadoDto Cliente(string nombre) => new(Guid.NewGuid(), nombre, EsOrigen: false);

    private static KpisDashboardDto Kpis(
        int vigentes = 0, int proximos = 0, int urgentes = 0, int vencidos = 0,
        int tasa = 100, bool sinCarteraAsignada = false, int trabajadoresActivos = 0, int centros = 0) =>
        new(
            TrabajadoresActivos: trabajadoresActivos,
            Centros: centros,
            DocumentosVencidos: vencidos,
            DocumentosUrgentes: urgentes,
            DocumentosProximos: proximos,
            DocumentosVigentes: vigentes,
            VisitasProgramadas: 0,
            TasaCumplimientoDocumental: tasa,
            SinCarteraAsignada: sinCarteraAsignada);

    [Fact]
    public void Suma_los_conteos_de_todos_los_clientes()
    {
        var porCliente = new List<(ClienteAutorizadoDto, KpisDashboardDto)>
        {
            (Cliente("Ibertec"), Kpis(vigentes: 80, urgentes: 20, trabajadoresActivos: 100, centros: 10)),
            (Cliente("EcoPlant"), Kpis(vigentes: 40, urgentes: 10, trabajadoresActivos: 50, centros: 4)),
        };

        var resultado = ObtenerKpisGlobalesQueryHandler.Fusionar(porCliente);

        resultado.TotalClientes.Should().Be(2);
        resultado.TrabajadoresActivos.Should().Be(150);
        resultado.Centros.Should().Be(14);
        resultado.DocumentosUrgentes.Should().Be(30);
    }

    /// <summary>
    /// Reproduce el DEFECTO 2 tal cual estaba: un Cliente Delegante sin
    /// cartera asignada llega con 0 documentos y tasa 100 (ver
    /// ObtenerKpisDashboardQueryHandler) — antes de este fix, ese 100 entraba
    /// en un promedio simple con el mismo peso que un Cliente con cartera
    /// completa y sí al día, inflando el SLA agregado con alcance que no
    /// existe.
    /// </summary>
    [Fact]
    public void La_media_de_SLA_excluye_a_los_clientes_sin_cartera_asignada()
    {
        var porCliente = new List<(ClienteAutorizadoDto, KpisDashboardDto)>
        {
            // Con cartera: 1 de 100 vigente — 1% de cumplimiento real.
            (Cliente("ConCartera"), Kpis(vigentes: 1, vencidos: 99, tasa: 1)),
            // Sin cartera: 0 documentos, tasa 100 por convención de "nada que evaluar".
            (Cliente("SinCartera"), Kpis(sinCarteraAsignada: true, tasa: 100)),
        };

        var resultado = ObtenerKpisGlobalesQueryHandler.Fusionar(porCliente);

        resultado.TasaCumplimientoDocumentalPromedio.Should().Be(1,
            "el 100% de un Cliente sin cartera no es SLA al día: no debe contar en la media");
        resultado.TasaCumplimientoDocumentalPromedio.Should().NotBe(50,
            "un promedio simple (1+100)/2 sería exactamente el defecto reproducido");
    }

    /// <summary>
    /// El indicador viaja también al DTO por fila — la Visión de cartera Gen2
    /// (bloqueada hasta este fix) lo necesita para pintar "sin cartera" en vez
    /// de un SLA que no existe.
    /// </summary>
    [Fact]
    public void SinCarteraAsignada_viaja_al_ClienteRiesgoDto_por_fila()
    {
        var porCliente = new List<(ClienteAutorizadoDto, KpisDashboardDto)>
        {
            (Cliente("SinCartera"), Kpis(sinCarteraAsignada: true, tasa: 100)),
        };

        var resultado = ObtenerKpisGlobalesQueryHandler.Fusionar(porCliente);

        resultado.ClientesConMasRiesgo.Should().ContainSingle().Which.SinCarteraAsignada.Should().BeTrue();
    }

    /// <summary>
    /// Un Cliente CON cartera pero sin ningún documento con vigencia (volumen
    /// cero) también debe quedar fuera de la media ponderada — mismo criterio
    /// que <c>ObtenerDashboardEjecutivoQueryHandler.PromedioPonderado</c>, sin
    /// necesitar una regla aparte de "sin cartera": pesa cero en la suma.
    /// </summary>
    [Fact]
    public void Un_cliente_con_cartera_pero_sin_documentos_con_vigencia_tampoco_distorsiona_la_media()
    {
        var porCliente = new List<(ClienteAutorizadoDto, KpisDashboardDto)>
        {
            (Cliente("ConDocumentos"), Kpis(vigentes: 1, vencidos: 9, tasa: 10)),
            (Cliente("SinDocumentos"), Kpis(tasa: 100)), // cartera real, cero documentos con vigencia.
        };

        var resultado = ObtenerKpisGlobalesQueryHandler.Fusionar(porCliente);

        resultado.TasaCumplimientoDocumentalPromedio.Should().Be(10);
    }

    [Fact]
    public void Sin_ningun_cliente_autorizado_la_media_es_100_no_0()
    {
        var resultado = ObtenerKpisGlobalesQueryHandler.Fusionar([]);

        resultado.TotalClientes.Should().Be(0);
        resultado.TasaCumplimientoDocumentalPromedio.Should().Be(100);
        resultado.ClientesConMasRiesgo.Should().BeEmpty();
    }

    [Fact]
    public void Cuando_todos_los_clientes_estan_sin_cartera_no_hay_media_que_presentar()
    {
        var porCliente = new List<(ClienteAutorizadoDto, KpisDashboardDto)>
        {
            (Cliente("SinCarteraUno"), Kpis(sinCarteraAsignada: true, tasa: 100)),
            (Cliente("SinCarteraDos"), Kpis(sinCarteraAsignada: true, tasa: 100)),
        };

        var resultado = ObtenerKpisGlobalesQueryHandler.Fusionar(porCliente);

        resultado.TasaCumplimientoDocumentalPromedio.Should().Be(100, "valor neutro del int, que no se pinta");
        resultado.HayCumplimientoDocumentalQueMedir.Should().BeFalse();
    }

    // ---------------------------------------------------------------- P2.4 / D-7

    private static KpisDashboardDto ConBloqueosYDatos(KpisDashboardDto kpis, int bloqueados = 0, bool sinDatos = false) =>
        kpis with { CentrosBloqueados = bloqueados, SinDatos = sinDatos };

    [Fact]
    public void Suma_los_Centros_de_Trabajo_bloqueados_y_los_lleva_a_cada_organizacion()
    {
        var porCliente = new List<(ClienteAutorizadoDto, KpisDashboardDto)>
        {
            (Cliente("Bloqueada"), ConBloqueosYDatos(Kpis(vigentes: 24, vencidos: 1, tasa: 96, centros: 2), bloqueados: 1)),
            (Cliente("AlDia"), Kpis(vigentes: 10, tasa: 100, centros: 1)),
            (Cliente("DosBloqueos"), ConBloqueosYDatos(Kpis(vigentes: 5, tasa: 100, centros: 3), bloqueados: 2)),
        };

        var resultado = ObtenerKpisGlobalesQueryHandler.Fusionar(porCliente);

        resultado.CentrosBloqueados.Should().Be(3);
        var porNombre = resultado.ClientesConMasRiesgo.ToDictionary(c => c.Nombre);
        porNombre["Bloqueada"].CentrosBloqueados.Should().Be(1);
        porNombre["DosBloqueos"].CentrosBloqueados.Should().Be(2);
        porNombre["AlDia"].CentrosBloqueados.Should().Be(0);
    }

    [Fact]
    public void Una_organizacion_con_un_Centro_de_Trabajo_bloqueado_no_admite_veredicto_verde_aunque_su_tasa_sea_96()
    {
        var porCliente = new List<(ClienteAutorizadoDto, KpisDashboardDto)>
        {
            (Cliente("Bloqueada"), ConBloqueosYDatos(Kpis(vigentes: 24, vencidos: 1, tasa: 96, centros: 2), bloqueados: 1)),
            (Cliente("AlDia"), Kpis(vigentes: 10, tasa: 100, centros: 1)),
        };

        var resultado = ObtenerKpisGlobalesQueryHandler.Fusionar(porCliente);

        var bloqueada = resultado.ClientesConMasRiesgo.Single(c => c.Nombre == "Bloqueada");
        bloqueada.TasaCumplimientoDocumental.Should().Be(96, "el porcentaje documental no cambia");
        bloqueada.AdmiteVeredictoVerde.Should().BeFalse("D-7: nunca «apto» con una Rechazada aplicable");
        resultado.ClientesConMasRiesgo.Single(c => c.Nombre == "AlDia").AdmiteVeredictoVerde.Should().BeTrue();
    }

    [Fact]
    public void Una_organizacion_sin_datos_no_admite_veredicto_verde_ni_pesa_en_la_media()
    {
        var porCliente = new List<(ClienteAutorizadoDto, KpisDashboardDto)>
        {
            (Cliente("SinDatos"), ConBloqueosYDatos(Kpis(tasa: 100), sinDatos: true)),
            (Cliente("ConDatos"), Kpis(vigentes: 50, vencidos: 50, tasa: 50, centros: 1)),
        };

        var resultado = ObtenerKpisGlobalesQueryHandler.Fusionar(porCliente);

        var sinDatos = resultado.ClientesConMasRiesgo.Single(c => c.Nombre == "SinDatos");
        sinDatos.SinDatos.Should().BeTrue();
        sinDatos.AdmiteVeredictoVerde.Should().BeFalse("su 100% es «nada que medir»");
        resultado.TasaCumplimientoDocumentalPromedio.Should().Be(50);
        resultado.HayCumplimientoDocumentalQueMedir.Should().BeTrue();
    }

    [Fact]
    public void Si_solo_hay_organizaciones_sin_datos_no_hay_media_que_presentar()
    {
        var porCliente = new List<(ClienteAutorizadoDto, KpisDashboardDto)>
        {
            (Cliente("SinDatosUno"), ConBloqueosYDatos(Kpis(tasa: 100), sinDatos: true)),
            (Cliente("SinDatosDos"), ConBloqueosYDatos(Kpis(tasa: 100), sinDatos: true)),
        };

        ObtenerKpisGlobalesQueryHandler.Fusionar(porCliente).HayCumplimientoDocumentalQueMedir.Should().BeFalse();
    }

    [Fact]
    public void El_ranking_de_riesgo_sigue_ordenando_por_vencidos_y_luego_urgentes()
    {
        var porCliente = new List<(ClienteAutorizadoDto, KpisDashboardDto)>
        {
            (Cliente("PocoRiesgo"), Kpis(vencidos: 1, urgentes: 5)),
            (Cliente("MuchoRiesgo"), Kpis(vencidos: 10, urgentes: 1)),
        };

        var resultado = ObtenerKpisGlobalesQueryHandler.Fusionar(porCliente);

        resultado.ClientesConMasRiesgo.Should().HaveCount(2);
        resultado.ClientesConMasRiesgo[0].Nombre.Should().Be("MuchoRiesgo");
    }
}
