using CaeManager.Application.Empresas;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.IntegrationTests;

/// <summary>PostgreSQL real: ejecutar en CI; no en la validación local de este incremento.</summary>
public class DuplicadosRazonSocialEmpresaTests : IAsyncLifetime
{
    private readonly string _conexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenant);
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_conexion);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Carrera_del_indice_de_razon_social_se_traduce_y_no_reintenta(bool baja)
    {
        await using var perdedora = CrearContexto(_tenant);
        var repo = new EmpresaRepository(perdedora);
        (await repo.ObtenerDuplicadaPorRazonSocialAsync(" Ganadora ")).Should().BeNull();
        await using (var ganadora = CrearContexto(_tenant))
        {
            var existente = new Empresa("Ganadora");
            if (baja) existente.MarcarComoEliminado(Guid.NewGuid());
            ganadora.Empresas.Add(existente);
            await ganadora.SaveChangesAsync();
        }
        perdedora.Empresas.Add(new Empresa(" Ganadora "));
        var behavior = new DuplicadosEmpresaBehavior<CrearEmpresaCommand, Result<Guid>>(
            new ServiceCollection().AddSingleton<IEmpresaRepository>(repo).BuildServiceProvider());
        var resultado = await behavior.Handle(new CrearEmpresaCommand(" Ganadora ", null, []), async ct =>
        {
            await perdedora.SaveChangesAsync(ct);
            return Result.Exito(Guid.NewGuid());
        }, default);
        resultado.Error.Codigo.Should().Be(baja ? "Empresa.RazonSocialDeBaja" : "Empresa.RazonSocialDuplicada");
        (await perdedora.SaveChangesAsync()).Should().Be(0);
        (await perdedora.Empresas.IgnoreQueryFilters().CountAsync(e => e.TenantId == _tenant)).Should().Be(1);
    }

    [Fact]
    public async Task Repositorio_incluye_bajas_respeta_mayusculas_y_cierra_por_Tenant_propietario()
    {
        await using (var ajeno = CrearContexto(Guid.NewGuid()))
        {
            ajeno.Empresas.Add(new Empresa("Reservada"));
            await ajeno.SaveChangesAsync();
        }
        await using (var contexto = CrearContexto(_tenant))
        {
            var repo = new EmpresaRepository(contexto);
            (await repo.ObtenerDuplicadaPorRazonSocialAsync("Reservada")).Should().BeNull();
            var baja = new Empresa("Reservada");
            baja.MarcarComoEliminado(Guid.NewGuid());
            contexto.Empresas.Add(baja);
            await contexto.SaveChangesAsync();
            (await repo.ObtenerDuplicadaPorRazonSocialAsync(" Reservada "))!.EstaEliminado.Should().BeTrue();
            (await repo.ObtenerDuplicadaPorRazonSocialAsync("Reservada", baja.Id)).Should().BeNull();
            (await repo.ObtenerDuplicadaPorRazonSocialAsync("RESERVADA")).Should().BeNull();
            contexto.Empresas.Add(new Empresa("RESERVADA"));
            await contexto.SaveChangesAsync();
        }
        await using var sinTenant = CrearContexto(null);
        (await new EmpresaRepository(sinTenant).ObtenerDuplicadaPorRazonSocialAsync("Reservada")).Should().BeNull();
    }

    private CaeManagerDbContext CrearContexto(Guid? tenant)
    {
        var actual = new TenantActualAmbiental { TenantId = tenant };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_conexion, o => o.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(actual)).Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), actual);
    }
}
