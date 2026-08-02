using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.CrearClienteDelegante;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Cobertura de P0-7 (docs/business/MATURITY_REVIEW.md): alta de un Cliente
/// Delegante nuevo, v1 restringida a Administrador de plataforma
/// (ADR-004 § 12.2). Contra Postgres real para probar tanto la autorización
/// (tenant de origen) como el sellado real de <c>TenantId</c> vía
/// <see cref="TenantSelladoInterceptor"/> en la fila de <c>ParametroSistema</c>
/// del tenant nuevo.
///
/// Usa <see cref="TenantActualDesdeAmbitoExplicito"/> en vez del
/// <c>TenantActualAmbiental</c> habitual de otros tests de integración:
/// el propio Command bajo prueba resuelve el tenant del <c>ParametroSistema</c>
/// nuevo con <c>AmbitoTenantExplicito</c> (mismo mecanismo que
/// <c>DelegacionDemoSeeder</c>), y <c>TenantActualAmbiental</c> es, a
/// propósito, un valor fijo que no lo consulta — aquí hace falta el mismo
/// comportamiento que la implementación Web real.
/// </summary>
public class CrearClienteDeleganteTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly ITenantActual _tenantActual = new TenantActualDesdeAmbitoExplicito();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }

    private static CrearClienteDeleganteCommandHandler CrearHandler(CaeManagerDbContext contexto, Guid? tenantOrigenId, Guid? usuarioId) =>
        new(
            new TenantRepository(contexto),
            new DelegacionTenantRepository(contexto),
            new AsignacionOperadorDelegadoRepository(contexto),
            new ParametroSistemaRepository(contexto),
            contexto,
            new CurrentUserServiceFalso(tenantOrigenId, usuarioId),
            contexto);

    [Fact]
    public async Task Un_administrador_de_plataforma_crea_el_tenant_la_delegacion_activa_y_su_propia_asignacion()
    {
        await using var contexto = CrearContexto();

        var tenantPlataforma = new Tenant("Hydra Plataforma de prueba", esPlataforma: true);
        contexto.Tenants.Add(tenantPlataforma);
        await contexto.SaveChangesAsync();

        var usuarioId = Guid.NewGuid();
        var handler = CrearHandler(contexto, tenantPlataforma.Id, usuarioId);

        var resultado = await handler.Handle(
            new CrearClienteDeleganteCommand($"Constructora de prueba {Guid.NewGuid():N}"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();

        var tenantClienteId = resultado.Valor;
        var tenantCliente = await contexto.Tenants.SingleAsync(t => t.Id == tenantClienteId);
        tenantCliente.EsPlataforma.Should().BeFalse();

        // IgnoreQueryFilters: el filtro global de EntidadConTenant exige
        // ITenantActual.TenantId == TenantId de la fila, y aquí ya salimos del
        // AmbitoTenantExplicito que el propio Command abrió para sellarla —
        // se re-aplica el filtro de tenant a mano con el Where explícito.
        var parametro = await contexto.ParametrosSistema.IgnoreQueryFilters()
            .SingleAsync(p => p.TenantId == tenantClienteId);
        parametro.Should().NotBeNull();

        var delegacion = await contexto.DelegacionesTenant.SingleAsync(d => d.TenantClienteId == tenantClienteId);
        delegacion.TenantConsultoraId.Should().Be(tenantPlataforma.Id);
        delegacion.Activa.Should().BeTrue();

        var asignacion = await contexto.AsignacionesOperadorDelegado.SingleAsync(a => a.DelegacionTenantId == delegacion.Id);
        asignacion.UsuarioId.Should().Be(usuarioId);
    }

    [Fact]
    public async Task Rechaza_el_alta_cuando_el_tenant_de_origen_no_es_la_plataforma()
    {
        await using var contexto = CrearContexto();

        var tenantNoPlataforma = new Tenant("Cliente normal de prueba");
        contexto.Tenants.Add(tenantNoPlataforma);
        await contexto.SaveChangesAsync();

        var handler = CrearHandler(contexto, tenantNoPlataforma.Id, Guid.NewGuid());

        var resultado = await handler.Handle(
            new CrearClienteDeleganteCommand("Constructora rechazada"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ClienteDelegante.SinPermiso");

        // 2, no 1: la migración siembra el tenant #1 (EsPlataforma = true) en
        // toda base de datos nueva — el de aquí es el segundo, y ninguno más
        // debió crearse tras el rechazo.
        (await contexto.Tenants.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Rechaza_un_nombre_de_tenant_duplicado()
    {
        await using var contexto = CrearContexto();

        var tenantPlataforma = new Tenant("Hydra Plataforma de prueba 2", esPlataforma: true);
        var nombreDuplicado = $"Constructora duplicada {Guid.NewGuid():N}";
        var tenantExistente = new Tenant(nombreDuplicado);
        contexto.Tenants.AddRange(tenantPlataforma, tenantExistente);
        await contexto.SaveChangesAsync();

        var handler = CrearHandler(contexto, tenantPlataforma.Id, Guid.NewGuid());

        var resultado = await handler.Handle(new CrearClienteDeleganteCommand(nombreDuplicado), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ClienteDelegante.NombreDuplicado");
    }

    private sealed class CurrentUserServiceFalso(Guid? tenantOrigenId, Guid? usuarioId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(usuarioId);
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>(null);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(tenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class TenantActualDesdeAmbitoExplicito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
