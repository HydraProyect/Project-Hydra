using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Q = CaeManager.Application.AsistenteIa.Candidatos.ObtenerCandidatosAsistenteQueryHandler;

namespace CaeManager.IntegrationTests.AsistenteIa;

/// <summary>
/// Lo que <c>CandidatosAsistenteTests</c> (Application.Tests) no puede probar:
/// que el fan-out real por <see cref="AmbitoTenantExplicito"/> sobre PostgreSQL
/// sella cada candidato con el Tenant del que sale de verdad, sin que las
/// Queries de selector crucen el filtro de Tenant. Es la garantía de que el
/// asistente nunca propone el Trabajador de un Tenant para una gestión en otro.
/// El alcance de cartera es un doble por Tenant, como el caché real de
/// <c>AlcanceDatosService</c>; su cálculo desde las Asignaciones de Cartera
/// tiene sus propios tests.
/// </summary>
public class CandidatosAsistenteMultiTenantTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly Dictionary<Guid, IAlcanceDatosService> _alcance = new();
    private CaeManagerDbContext _dbContext = null!;
    private ServiceProvider _servicios = null!;
    private Guid _tenantOrigen;
    private Guid _tenantDelegante;
    private Guid _tenantSinCartera;
    private (Guid Centro, Guid Trabajador) _origen;
    private (Guid Centro, Guid Trabajador) _delegante;
    private Guid _centroDeleganteFueraDeCartera;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualPorAmbito();
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();

        var origen = new Tenant("Operador Candidatos");
        var delegante = new Tenant("Delegante Candidatos");
        var sinCartera = new Tenant("Sin Cartera Candidatos");
        _dbContext.Tenants.AddRange(origen, delegante, sinCartera);
        (_tenantOrigen, _tenantDelegante, _tenantSinCartera) = (origen.Id, delegante.Id, sinCartera.Id);

        foreach (var tenant in new[] { _tenantDelegante, _tenantSinCartera })
        {
            var delegacion = new DelegacionTenant(_tenantOrigen, tenant);
            _dbContext.DelegacionesTenant.Add(delegacion);
            _dbContext.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(delegacion.Id, _usuario, "GestorCae"));
        }
        await _dbContext.SaveChangesAsync();

        _origen = await SembrarAsync(_tenantOrigen, "Origen");
        _delegante = await SembrarAsync(_tenantDelegante, "Delegante");
        await SembrarAsync(_tenantSinCartera, "SinCartera");

        using (AmbitoTenantExplicito.Establecer(_tenantDelegante))
        {
            var cliente = await _dbContext.Empresas.SingleAsync(e => e.RazonSocial == "Delegante Cliente");
            var empresa = await _dbContext.Empresas.SingleAsync(e => e.RazonSocial == "Delegante Empresa");
            var fuera = new Centro(cliente.Id, empresa.Id, "Nave Norte Anexo");
            _dbContext.Centros.Add(fuera);
            await _dbContext.SaveChangesAsync();
            _centroDeleganteFueraDeCartera = fuera.Id;
        }

        _alcance[_tenantOrigen] = new AlcanceDatosServiceFalso();
        // En el Delegante la cartera cubre un Centro de los dos.
        _alcance[_tenantDelegante] = new AlcanceDatosServiceFalso(
            clienteIds: await IdsAsync(_tenantDelegante, "Delegante Cliente"),
            centroIds: [_delegante.Centro],
            trabajadorIds: [_delegante.Trabajador]);
        _alcance[_tenantSinCartera] = new AlcanceDatosServiceFalso(clienteIds: []);

        _servicios = ConstruirServicios(tenantActual);
    }

    public async Task DisposeAsync()
    {
        _servicios.Dispose();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task Cada_candidato_sale_sellado_con_su_Tenant_y_solo_de_la_cartera()
    {
        var resultado = await _servicios.GetRequiredService<IMediator>().Send(
            new ObtenerCandidatosAsistenteQuery([Q.CampoTenant, Q.CampoCentro, Q.CampoTrabajadores]));

        resultado.Tenants.Select(t => t.TenantId).Should().Equal(_tenantOrigen, _tenantDelegante);

        var centros = resultado.PorCampo[Q.CampoCentro];
        centros.Should().BeEquivalentTo(
        [
            new CandidatoSelladoDto(_origen.Centro, "Nave Norte · Origen Cliente · en Operador Candidatos", _tenantOrigen),
            new CandidatoSelladoDto(_delegante.Centro, "Nave Norte · Delegante Cliente · en Delegante Candidatos", _tenantDelegante),
        ]);
        centros.Select(c => c.Id).Should().NotContain(_centroDeleganteFueraDeCartera);

        resultado.PorCampo[Q.CampoTrabajadores].Should().BeEquivalentTo(
        [
            new CandidatoSelladoDto(_origen.Trabajador, "Ana Ruiz · en Operador Candidatos", _tenantOrigen),
            new CandidatoSelladoDto(_delegante.Trabajador, "Ana Ruiz · en Delegante Candidatos", _tenantDelegante),
        ]);
    }

    [Fact]
    public async Task El_Trabajador_de_un_Tenant_con_el_Centro_de_otro_no_se_puede_confirmar()
    {
        var candidatos = await _servicios.GetRequiredService<IMediator>().Send(
            new ObtenerCandidatosAsistenteQuery([Q.CampoCentro, Q.CampoTrabajadores]));

        var destino = ResolucionTenantDestino.Resolver(candidatos,
            [new(Q.CampoCentro, _delegante.Centro, 90), new(Q.CampoTrabajadores, _origen.Trabajador, 90)]);

        destino.Situacion.Should().Be(SituacionTenantDestino.Mezcla);
        destino.Bloquea.Should().BeTrue();
    }

    private ServiceProvider ConstruirServicios(ITenantActual tenantActual)
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton(tenantActual);
        servicios.AddSingleton<IUnitOfWork>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Tenants.ITenantsQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(_dbContext);
        servicios.AddSingleton<IAlcanceDatosService>(new AlcancePorTenant(_alcance));
        servicios.AddSingleton<ICurrentUserService>(new CurrentUserServiceFalso(_usuario, tenantOrigenId: _tenantOrigen));
        return servicios.BuildServiceProvider();
    }

    private async Task<(Guid Centro, Guid Trabajador)> SembrarAsync(Guid tenantId, string prefijo)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);

        var cliente = Empresa.CrearComoCliente($"{prefijo} Cliente", "B12345674", false, null, null);
        var empresa = new Empresa($"{prefijo} Empresa");
        _dbContext.Empresas.AddRange(cliente, empresa);
        await _dbContext.SaveChangesAsync();

        // Mismo nombre de Centro y de persona en todos los Tenants: el sello es
        // lo único que los distingue.
        var centro = new Centro(cliente.Id, empresa.Id, "Nave Norte");
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "Ruiz", GenerarDni(tenantId));
        _dbContext.Centros.Add(centro);
        _dbContext.Trabajadores.Add(trabajador);
        await _dbContext.SaveChangesAsync();
        return (centro.Id, trabajador.Id);
    }

    private async Task<IReadOnlyList<Guid>> IdsAsync(Guid tenantId, string razonSocial)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        return await _dbContext.Empresas.Where(e => e.RazonSocial == razonSocial).Select(e => e.Id).ToListAsync();
    }

    private static string GenerarDni(Guid semilla)
    {
        const string letrasControl = "TRWAGMYFPDXBNJZSQVHLCKE";
        var numero = Math.Abs(semilla.GetHashCode()) % 100_000_000;
        return $"{numero:D8}{letrasControl[numero % 23]}";
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class AlcancePorTenant(Dictionary<Guid, IAlcanceDatosService> alcance) : IAlcanceDatosService
    {
        private IAlcanceDatosService Actual => alcance[AmbitoTenantExplicito.TenantIdActual
            ?? throw new InvalidOperationException("Alcance leído fuera de un ámbito de Tenant.")];

        public Task<bool> TieneAccesoTotalAsync(CancellationToken c = default) => Actual.TieneAccesoTotalAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerClienteIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerClienteIdsVisiblesAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerCentroIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerCentroIdsVisiblesAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerCentroIdsParaGestionAsync(CancellationToken c = default) => Actual.ObtenerCentroIdsParaGestionAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerEmpresaIdsVisiblesAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsParaGestionAsync(CancellationToken c = default) => Actual.ObtenerEmpresaIdsParaGestionAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerSubcontrataIdsVisiblesAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsParaGestionAsync(CancellationToken c = default) => Actual.ObtenerSubcontrataIdsParaGestionAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerTrabajadorIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerTrabajadorIdsVisiblesAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerVehiculoIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerVehiculoIdsVisiblesAsync(c);
        public Task<bool> ConexionIntegracionVisibleAsync(Guid id, CancellationToken c = default) => Actual.ConexionIntegracionVisibleAsync(id, c);
    }
}
