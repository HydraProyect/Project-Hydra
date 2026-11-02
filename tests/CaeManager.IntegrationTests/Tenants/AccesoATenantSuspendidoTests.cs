using CaeManager.Application.Tenants;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.ApiKeys;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Quien opera un Tenant beneficiario no entra por la Asignación de Operación ni por la delegación
/// heredada mientras ese Tenant esté suspendido (decisión D-3), y vuelve a entrar al reactivarlo. El
/// predicado se traduce a SQL de verdad: un contexto de memoria no lo demostraría.
/// </summary>
public class AccesoATenantSuspendidoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _beneficiario = new("Tenant beneficiario");
    private readonly Tenant _operador = new("Operador CAE externo");
    private readonly Guid _administradorDelOperador = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var ahora = DateTime.UtcNow;
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
        contexto.Tenants.AddRange(_beneficiario, _operador);
        contexto.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
            _beneficiario.Id, _operador.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora.AddMinutes(-5), null, ahora));
        var delegacion = new DelegacionTenant(_operador.Id, _beneficiario.Id);
        contexto.DelegacionesTenant.Add(delegacion);
        contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
            new AsignacionOperadorDelegado(delegacion.Id, _administradorDelOperador, Roles.Administrador));
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task La_suspension_cierra_las_dos_vias_y_reactivar_las_abre()
    {
        (await PorOperacionAsync()).Should().BeTrue();
        (await PorViaHeredadaAsync()).Should().BeTrue();

        await CambiarEstadoAsync(t => t.Suspender());
        (await PorOperacionAsync()).Should().BeFalse();
        (await PorViaHeredadaAsync()).Should().BeFalse();

        await CambiarEstadoAsync(t => t.Reactivar());
        (await PorOperacionAsync()).Should().BeTrue();
        (await PorViaHeredadaAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task El_estado_se_lee_de_la_base_aunque_el_contexto_siga_la_fila()
    {
        await using var circuito = CrearContexto();
        await circuito.Tenants.SingleAsync(t => t.Id == _beneficiario.Id);
        var estado = new EstadoOperativoTenant(circuito);
        (await estado.EstaSuspendidoAsync(_beneficiario.Id)).Should().BeFalse();

        await CambiarEstadoAsync(t => t.Suspender());

        (await estado.EstaSuspendidoAsync(_beneficiario.Id)).Should().BeTrue();
        (await estado.EstaSuspendidoAsync(Guid.NewGuid())).Should().BeFalse("una fila inexistente no cuenta como suspendida");
    }

    private async Task<bool> PorOperacionAsync()
    {
        await using var contexto = CrearContexto();
        return await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
            contexto, _administradorDelOperador, _operador.Id, _beneficiario.Id, "Administrador", DateTime.UtcNow, CancellationToken.None) is not null;
    }

    private async Task<bool> PorViaHeredadaAsync()
    {
        await using var contexto = CrearContexto();
        return await TenantsBeneficiariosAutorizados.AutorizadoPorViaHeredadaAsync(
            contexto, _administradorDelOperador, _beneficiario.Id, DateTime.UtcNow, CancellationToken.None);
    }

    private async Task CambiarEstadoAsync(Action<Tenant> cambio)
    {
        await using var contexto = CrearContexto();
        cambio(await contexto.Tenants.SingleAsync(t => t.Id == _beneficiario.Id));
        await contexto.SaveChangesAsync();
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _beneficiario.Id };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
