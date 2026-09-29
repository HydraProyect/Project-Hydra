using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.GuardarLogoTenant;
using CaeManager.Application.Tenants.Logo;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Revisión Codex, ronda 2: tras un conflicto de concurrencia el contexto —que en Blazor Server vive lo
/// que el circuito— no puede conservar el Tenant modificado con el token ya obsoleto ni los registros
/// de auditoría añadidos: todo guardado posterior del mismo contexto volvería a fallar.
/// </summary>
public class DescarteTrasConflictoLogoTenantTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private Guid _tenantId;

    public async Task InitializeAsync()
    {
        await using var contexto = Crear();
        await contexto.Database.MigrateAsync();
        var tenant = new Tenant("Tenant con logo en conflicto");
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();
        _tenantId = tenant.Id;
        tenant.EstablecerLogo($"{_tenantId:N}/inicial.png", "aaaaaaaaaaaaaaaa", DateTime.UtcNow);
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task Tras_perder_la_carrera_un_guardado_posterior_del_mismo_contexto_funciona()
    {
        await using var contexto = Crear();
        var almacen = new AlmacenQueGanaLaCarrera(this);
        var handler = new GuardarLogoTenantCommandHandler(
            new TenantActualFijo(_tenantId), new AutorizaTodo(), new ConversorFijo(),
            new TenantRepository(contexto), almacen, contexto, contexto,
            NullLogger<GuardarLogoTenantCommandHandler>.Instance);

        var accion = () => handler.Handle(new GuardarLogoTenantCommand([1]), CancellationToken.None);
        await accion.Should().ThrowAsync<DbUpdateConcurrencyException>("otra subida ganó entre la lectura y el guardado");

        // Mismo contexto: la instancia rastreada ya no puede arrastrar el token obsoleto.
        var tenant = await new TenantRepository(contexto).ObtenerPorIdAsync(_tenantId);
        tenant!.LogoVersion.Should().Be("bbbbbbbbbbbbbbbb", "se relee lo que ganó, no la instancia obsoleta");
        tenant.RetirarLogo(DateTime.UtcNow);
        var guardar = () => contexto.SaveChangesAsync();
        await guardar.Should().NotThrowAsync("el estado del guardado fallido se descartó");
    }

    private async Task GanarLaCarreraAsync()
    {
        await using var otro = Crear();
        var tenant = await otro.Tenants.SingleAsync(t => t.Id == _tenantId);
        tenant.EstablecerLogo($"{_tenantId:N}/ganadora.png", "bbbbbbbbbbbbbbbb", DateTime.UtcNow);
        await otro.SaveChangesAsync();
    }

    private CaeManagerDbContext Crear()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new AuditoriaInterceptor(new ActorFalso()), new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ActorFalso : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(Guid.NewGuid()));

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(Guid.NewGuid());
    }

    private sealed class TenantActualFijo(Guid id) : ITenantActual
    {
        public Guid? TenantId => id;
    }

    private sealed class AutorizaTodo : IAutorizacionLogoTenant
    {
        public Task<bool> PuedeEscribirAsync(Guid tenantObjetivoId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class ConversorFijo : IConversorLogoTenantService
    {
        public byte[] ConvertirAPng(byte[] imagenOriginal) => [9, 9, 9];
    }

    /// <summary>Guardar el blob es el hueco entre leer el Tenant y guardarlo: ahí gana la otra subida.</summary>
    private sealed class AlmacenQueGanaLaCarrera(DescarteTrasConflictoLogoTenantTests prueba) : IFileStorageService
    {
        public async Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
        {
            await prueba.GanarLaCarreraAsync();
            return $"{prueba._tenantId:N}/perdedora.png";
        }

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
