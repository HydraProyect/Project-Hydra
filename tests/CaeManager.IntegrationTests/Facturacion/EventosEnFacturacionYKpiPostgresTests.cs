using CaeManager.Application.Common;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Facturacion;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Facturacion;

/// <summary>
/// Los Proyectos de tipo Evento contra PostgreSQL real: la pantalla de Facturación los excluye
/// (<c>Proyectos.Tipo</c> se filtra en la propia consulta) y la estimación de KPI también. Comprueba que las dos
/// consultas traducen a SQL con el filtro; la cobertura por concepto y las mutaciones están en
/// <c>EventosEnFacturacionYKpiTests</c> (Application.Tests).
/// </summary>
public class EventosEnFacturacionYKpiPostgresTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _contexto = null!;
    private Guid _tenant;
    private Guid _cliente;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _contexto = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _contexto.Database.MigrateAsync();

        var tenant = new Tenant("Tenant beneficiario con eventos");
        _contexto.Tenants.Add(tenant);
        await _contexto.SaveChangesAsync();
        _tenant = tenant.Id;

        using var ambito = AmbitoTenantExplicito.Establecer(_tenant);
        var cliente = Empresa.CrearComoCliente("Festivales del Sur", "B12345674", false, null, null);
        var empresa = new Empresa("Montajes Escenario S.L.", "B87654323");
        _contexto.Empresas.AddRange(cliente, empresa);
        await _contexto.SaveChangesAsync();
        _cliente = cliente.Id;

        var centro = new Centro(cliente.Id, empresa.Id, "Recinto ferial");
        _contexto.Centros.Add(centro);
        await _contexto.SaveChangesAsync();

        var obra = Proyecto.Crear(cliente.Id, centro.Id, "Montaje de gradas", new DateOnly(2026, 3, 10), null, null);
        obra.Cerrar(new DateOnly(2026, 3, 20));
        var evento = Proyecto.Crear(
            cliente.Id, centro.Id, "Festival de primavera", new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 8), null, TipoProyecto.Evento);
        _contexto.Proyectos.AddRange(obra, evento);
        await _contexto.SaveChangesAsync();

        var tecnicoDeObra = Trabajador.DeEmpresa(empresa.Id, "Homer", "Simpson", "77189989B");
        var tecnicoDeEvento = Trabajador.DeEmpresa(empresa.Id, "Marge", "Bouvier", "12345678Z");
        _contexto.Trabajadores.AddRange(tecnicoDeObra, tecnicoDeEvento);
        await _contexto.SaveChangesAsync();

        _contexto.ProyectosTecnicos.Add(new ProyectoTecnico(obra.Id, tecnicoDeObra.Id, new DateOnly(2026, 3, 10)));
        _contexto.ProyectosTecnicos.Add(new ProyectoTecnico(evento.Id, tecnicoDeEvento.Id, new DateOnly(2026, 3, 5)));
        _contexto.TarifasCliente.Add(TarifaCliente.Crear(cliente.Id, ConceptoFacturable.TecnicoAsignadoProyecto, 100m, "EUR"));
        _contexto.TarifasCliente.Add(TarifaCliente.Crear(cliente.Id, ConceptoFacturable.DiaProyectoAbierto, 1m, "EUR"));
        await _contexto.SaveChangesAsync();
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    public async Task DisposeAsync()
    {
        await _contexto.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task La_pantalla_de_facturacion_solo_cuenta_la_obra()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenant);
        var handler = new ObtenerResumenFacturacionQueryHandler(_contexto, _contexto, _contexto, _contexto, _contexto, _contexto, _contexto, _contexto);

        var resumen = await handler.Handle(new ObtenerResumenFacturacionQuery(_cliente, 2026, 3), CancellationToken.None);

        resumen!.Lineas.Single(l => l.Concepto == ConceptoFacturable.TecnicoAsignadoProyecto).Unidades.Should().Be(1);
        resumen.Lineas.Single(l => l.Concepto == ConceptoFacturable.DiaProyectoAbierto).Unidades.Should().Be(11, "del 10 al 20 de marzo; el evento no suma");
    }

    [Fact]
    public async Task La_estimacion_de_KPI_solo_suma_la_obra()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenant);
        var handler = new ObtenerCatalogoKpisQueryHandler(
            _contexto, _contexto, null!, _contexto, _contexto, new AlcanceDatosServiceFalso(), null!,
            _contexto, _contexto, _contexto, _contexto, _contexto, _contexto);

        var estimacion = await handler.CalcularFacturacionEstimadaAsync(
            PeriodoKpi.DeFechas(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)), CancellationToken.None);

        estimacion.Should().Be(100m + 11 * 1m, "un técnico y 11 días, los de la obra; el evento no suma");
    }
}
