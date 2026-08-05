using CaeManager.Application.Trabajadores.Commands.AsignarAliasTrabajador;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Trabajadores;

/// <summary>
/// Pieza 6: alta de un clic del Alias sugerido por
/// DetectarCamposDocumentoQuery (ver Trabajador.AsignarAlias).
/// </summary>
public class AsignarAliasTrabajadorCommandTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _trabajadorId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var empresa = new Empresa("Empresa Alias S.L.", "B87654323");
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Francisco", "Villa", "77189989B");
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        _trabajadorId = trabajador.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Asigna_el_alias_sugerido_al_trabajador()
    {
        await using var contexto = CrearContexto();
        var repositorio = new TrabajadorRepository(contexto);
        var handler = new AsignarAliasTrabajadorCommandHandler(repositorio, new AlcanceDatosServiceFalso(), contexto);

        var resultado = await handler.Handle(new AsignarAliasTrabajadorCommand(_trabajadorId, "Pancho Villa"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var actualizado = await contexto.Trabajadores.SingleAsync(t => t.Id == _trabajadorId);
        actualizado.Alias.Should().Be("Pancho Villa");
    }

    [Fact]
    public async Task Falla_con_un_trabajador_que_no_existe()
    {
        await using var contexto = CrearContexto();
        var repositorio = new TrabajadorRepository(contexto);
        var handler = new AsignarAliasTrabajadorCommandHandler(repositorio, new AlcanceDatosServiceFalso(), contexto);

        var resultado = await handler.Handle(new AsignarAliasTrabajadorCommand(Guid.NewGuid(), "Pancho Villa"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Trabajador.NoEncontrado");
    }

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
