using CaeManager.Application.Centros;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Application.Visitas.Queries.ObtenerVisitasParaCalendario;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Visitas;

/// <summary>
/// FS-11 (auditoría UX de flujos sin salida, 2026-09-24): una Visita cancelada sale
/// de todo lo activo —la lista activa, lo urgente que alimenta Mi trabajo y la
/// Bandeja, el Calendario, la próxima visita de cada Centro y los contadores de
/// Inicio— pero se conserva en el historial. Contra PostgreSQL real, porque cada
/// exclusión es un filtro SQL.
/// </summary>
public class VisitaCanceladaFueraDeLoActivoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = DateOnly.FromDateTime(DateTime.UtcNow);
    private Guid _centroId;
    private Guid _activa;
    private Guid _cancelada;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        contexto.ParametrosSistema.Add(new ParametroSistema(30, 15, horasAvisoVisita: 48, horasCriticasVisita: 24));
        var cliente = Empresa.CrearComoCliente("Cliente Visita Cancelada S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa Visita Cancelada S.L.", "B87654323");
        contexto.Empresas.AddRange(cliente, empresa);
        var centro = new Centro(cliente.Id, empresa.Id, "Centro Visita Cancelada");
        contexto.Centros.Add(centro);

        // Las dos empiezan mañana: activas, urgentes y en el mes del Calendario.
        // Solo las distingue la cancelación.
        var activa = new Visita(centro.Id, _hoy.AddDays(1), _hoy.AddDays(1), notas: null);
        var cancelada = new Visita(centro.Id, _hoy.AddDays(1), _hoy.AddDays(1), notas: null);
        cancelada.Cancelar(DateTime.UtcNow, "Obra aplazada");
        contexto.Visitas.AddRange(activa, cancelada);
        await contexto.SaveChangesAsync();

        (_centroId, _activa, _cancelada) = (centro.Id, activa.Id, cancelada.Id);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData(true, false)]  // lista activa
    [InlineData(true, true)]   // Mi trabajo y Bandeja: SoloActivas + SoloUrgentes
    [InlineData(false, true)]  // solo urgentes
    public async Task La_lista_activa_y_lo_urgente_no_incluyen_la_cancelada(bool soloActivas, bool soloUrgentes)
    {
        await using var lectura = CrearContexto();

        var resultado = await HandlerLista(lectura).Handle(
            new ObtenerVisitasQuery(Busqueda: null, SoloActivas: soloActivas, NotificadoCliente: null, SoloUrgentes: soloUrgentes),
            CancellationToken.None);

        resultado.Elementos.Select(v => v.Id).Should().Equal([_activa], "la activa es el control positivo; la cancelada no aparece");
        resultado.TotalElementos.Should().Be(1);
    }

    [Fact]
    public async Task El_historial_conserva_la_cancelada_marcada_y_sin_urgencia()
    {
        await using var lectura = CrearContexto();

        var resultado = await HandlerLista(lectura).Handle(
            new ObtenerVisitasQuery(Busqueda: null, SoloActivas: false, NotificadoCliente: null), CancellationToken.None);

        resultado.Elementos.Select(v => v.Id).Should().BeEquivalentTo([_activa, _cancelada]);
        var cancelada = resultado.Elementos.Single(v => v.Id == _cancelada);
        cancelada.EstaCancelada.Should().BeTrue();
        cancelada.MotivoCancelacion.Should().Be("Obra aplazada");
        cancelada.NivelUrgencia.Should().Be(NivelUrgenciaVisita.Normal, "una cancelada no es urgente aunque empiece mañana");
        resultado.Elementos.Single(v => v.Id == _activa).NivelUrgencia.Should().Be(NivelUrgenciaVisita.Critica, "control positivo");
    }

    [Fact]
    public async Task El_Calendario_no_pinta_la_cancelada()
    {
        await using var lectura = CrearContexto();
        var manana = _hoy.AddDays(1);

        var visitas = await new ObtenerVisitasParaCalendarioQueryHandler(lectura, lectura, lectura, new AlcanceDatosServiceFalso())
            .Handle(new ObtenerVisitasParaCalendarioQuery(manana.Year, manana.Month), CancellationToken.None);

        visitas.Select(v => v.Id).Should().Equal([_activa]);
    }

    [Fact]
    public async Task La_proxima_visita_del_Centro_no_es_la_cancelada()
    {
        await using var lectura = CrearContexto();

        var porCentro = await new ObtenerProximaVisitaPorCentroQueryHandler(lectura)
            .Handle(new ObtenerProximaVisitaPorCentroQuery([_centroId]), CancellationToken.None);

        porCentro[_centroId].Select(v => v.VisitaId).Should().Equal([_activa]);
    }

    [Fact]
    public async Task Los_contadores_de_Inicio_no_cuentan_la_cancelada()
    {
        await using var lectura = CrearContexto();
        var calculo = new CalculoEstadoCentroService(lectura, lectura, lectura, lectura, lectura, lectura);

        var kpis = await new ObtenerKpisDashboardQueryHandler(lectura, lectura, lectura, lectura, lectura, new AlcanceDatosServiceFalso(), calculo)
            .Handle(new ObtenerKpisDashboardQuery(), CancellationToken.None);

        kpis.VisitasProgramadas.Should().Be(1);
        kpis.VisitasUrgentes.Should().Be(1);
    }

    private static ObtenerVisitasQueryHandler HandlerLista(CaeManagerDbContext contexto) =>
        new(contexto, contexto, contexto, contexto, contexto, contexto, new AlcanceDatosServiceFalso());

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
