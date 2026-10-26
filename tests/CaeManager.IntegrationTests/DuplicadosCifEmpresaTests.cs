using CaeManager.Application.Common;
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
using Npgsql;

namespace CaeManager.IntegrationTests;

/// <summary>Z5-7: PostgreSQL real. Esta suite se ejecuta en CI, no en la validación local de este incremento.</summary>
public class DuplicadosCifEmpresaTests : IAsyncLifetime
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
    public async Task Carrera_del_indice_de_Cif_devuelve_error_y_no_reintenta_la_escritura(bool baja)
    {
        await using var perdedora = CrearContexto(_tenant);
        var repo = new EmpresaRepository(perdedora);
        (await repo.ExisteConCifAsync(" b12345674 ")).Should().BeFalse();
        await using (var ganadora = CrearContexto(_tenant))
        {
            var existente = new Empresa("Ganadora", "B12345674");
            if (baja)
                existente.MarcarComoEliminado(Guid.NewGuid());
            ganadora.Empresas.Add(existente);
            await ganadora.SaveChangesAsync();
        }
        perdedora.Empresas.Add(new Empresa("Perdedora", "b12345674"));
        var behavior = new DuplicadosEmpresaBehavior<CrearEmpresaCommand, Result<Guid>>(
            new ServiceCollection().AddSingleton<IEmpresaRepository>(repo).BuildServiceProvider());
        var resultado = await behavior.Handle(new CrearEmpresaCommand("Perdedora", "b12345674", []), async ct =>
        {
            await perdedora.SaveChangesAsync(ct);
            return Result.Exito(Guid.NewGuid());
        }, default);
        resultado.Error.Codigo.Should().Be(baja ? "Empresa.CifDeBaja" : "Empresa.CifDuplicado");
        (await perdedora.SaveChangesAsync()).Should().Be(0);
        (await perdedora.Empresas.CountAsync(e => e.RazonSocial == "Perdedora")).Should().Be(0);
    }

    [Fact]
    public async Task Otro_indice_unico_no_se_traduce_a_Cif_duplicado()
    {
        await using var contexto = CrearContexto(_tenant);
        contexto.Empresas.Add(new Empresa("Mismo nombre", "B12345674"));
        await contexto.SaveChangesAsync();
        contexto.Empresas.Add(new Empresa("Mismo nombre", "P1234567D"));
        var accion = () => contexto.SaveChangesAsync();
        var error = await accion.Should().ThrowAsync<DatoEmpresaDuplicadoException>();
        error.Which.Should().NotBeOfType<CifEmpresaDuplicadoException>();
        error.Which.EsRazonSocial.Should().BeTrue();
        ((DbUpdateException)error.Which.InnerException!).InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be("IX_Empresas_TenantId_RazonSocial");
    }

    [Fact]
    public async Task Repositorio_incluye_bajas_y_cierra_por_Tenant_propietario_incluso_sin_Rls()
    {
        var otro = Guid.NewGuid();
        await using (var contexto = CrearContexto(otro))
        {
            var empresa = new Empresa("Ajena", "B12345674");
            empresa.MarcarComoEliminado(Guid.NewGuid());
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();
        }
        await using (var contexto = CrearContexto(_tenant))
        {
            var repo = new EmpresaRepository(contexto);
            (await repo.ObtenerDuplicadaPorCifAsync(" b12345674 ")).Should().BeNull();
            var baja = new Empresa("Local", "B12345674");
            baja.MarcarComoEliminado(Guid.NewGuid());
            contexto.Empresas.Add(baja);
            await contexto.SaveChangesAsync();
            (await repo.ObtenerDuplicadaPorCifAsync(" b12345674 "))!.EstaEliminado.Should().BeTrue();
            (await repo.ObtenerDuplicadaPorCifAsync("B12345674", baja.Id)).Should().BeNull();
        }
        await using var sinTenant = CrearContexto(null);
        (await new EmpresaRepository(sinTenant).ObtenerDuplicadaPorCifAsync("B12345674")).Should().BeNull();
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
