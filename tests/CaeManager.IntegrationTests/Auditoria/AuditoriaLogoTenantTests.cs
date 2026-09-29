using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// I9 del contrato del selector de Tenant: subir y retirar el logo se audita
/// separando el Actor real del Usuario simulado. Una Sesión Privilegiada de
/// Soporte TALVEG con Aprovisionamiento que sube el logo mientras simula a un
/// Administrador del Tenant propietario no puede quedar atribuida solo al
/// simulado.
/// </summary>
public class AuditoriaLogoTenantTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantAmbiental = Guid.NewGuid();
    private readonly Guid _actorReal = Guid.NewGuid();
    private readonly Guid _simulado = Guid.NewGuid();
    private readonly Guid _sesion = Guid.NewGuid();
    private Guid _tenantId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(ActorAuditoria.SinResolver);
        await contexto.Database.MigrateAsync();
        var tenant = new Tenant("Tenant con logo");
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();
        _tenantId = tenant.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Subir_el_logo_en_sesion_privilegiada_registra_al_simulado_como_autor_y_conserva_al_actor_real()
    {
        var actor = new ActorAuditoria(_actorReal, _simulado, TipoViaAcceso.SesionPrivilegiada, _sesion);

        await using (var contexto = CrearContexto(actor))
        {
            var tenant = await contexto.Tenants.SingleAsync(t => t.Id == _tenantId);
            tenant.EstablecerLogo($"{_tenantId:N}/logo-1.png", "0123456789abcdef", DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        var registro = await UltimoRegistroDelTenantAsync();

        registro.UsuarioId.Should().Be(_simulado, "el autor visible es a quien se simula");
        registro.ActorRealUsuarioId.Should().Be(_actorReal, "el Actor real no se pierde");
        registro.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.SesionPrivilegiada);
        registro.ViaAccesoId.Should().Be(_sesion);
    }

    [Fact]
    public async Task Retirar_el_logo_en_sesion_privilegiada_tambien_conserva_al_actor_real()
    {
        await using (var contexto = CrearContexto(ActorAuditoria.Normal(Guid.NewGuid())))
        {
            var tenant = await contexto.Tenants.SingleAsync(t => t.Id == _tenantId);
            tenant.EstablecerLogo($"{_tenantId:N}/logo-1.png", "0123456789abcdef", DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        var actor = new ActorAuditoria(_actorReal, _simulado, TipoViaAcceso.SesionPrivilegiada, _sesion);
        await using (var contexto = CrearContexto(actor))
        {
            var tenant = await contexto.Tenants.SingleAsync(t => t.Id == _tenantId);
            tenant.RetirarLogo(DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        var registro = await UltimoRegistroDelTenantAsync();

        registro.UsuarioId.Should().Be(_simulado);
        registro.ActorRealUsuarioId.Should().Be(_actorReal);
        registro.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.SesionPrivilegiada);
    }

    [Fact]
    public async Task Subir_el_logo_como_Administrador_del_Tenant_registra_al_mismo_usuario_como_autor_y_actor_real()
    {
        var administrador = Guid.NewGuid();

        await using (var contexto = CrearContexto(ActorAuditoria.Normal(administrador)))
        {
            var tenant = await contexto.Tenants.SingleAsync(t => t.Id == _tenantId);
            tenant.EstablecerLogo($"{_tenantId:N}/logo-2.png", "fedcba9876543210", DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        var registro = await UltimoRegistroDelTenantAsync();

        registro.UsuarioId.Should().Be(administrador);
        registro.ActorRealUsuarioId.Should().Be(administrador);
        registro.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.Normal);
    }

    private async Task<RegistroAuditoria> UltimoRegistroDelTenantAsync()
    {
        await using var contexto = CrearContexto(ActorAuditoria.SinResolver);

        return await contexto.RegistrosAuditoria
            .IgnoreQueryFilters()
            .Where(r => r.EntidadTipo == nameof(Tenant) && r.EntidadId == _tenantId)
            .OrderByDescending(r => r.FechaUtc)
            .FirstAsync();
    }

    private CaeManagerDbContext CrearContexto(ActorAuditoria actor)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantAmbiental };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new AuditoriaInterceptor(new ActorAuditoriaFalso(actor)), new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ActorAuditoriaFalso(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }
}
