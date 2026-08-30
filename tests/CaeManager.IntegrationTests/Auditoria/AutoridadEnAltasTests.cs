using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Proyectos.Commands.CrearProyecto;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Centro = CaeManager.Domain.Centros.Centro;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// Auditoría Módulo 5, hallazgo crítico 5/9: las altas de Centro, Trabajador
/// y Proyecto solo comprobaban que los Ids referenciados EXISTIERAN en el
/// tenant, no que el actor tuviera autoridad de cartera sobre ellos. Un
/// gestor podía crear datos dentro de la cartera de otro, o incorporar un
/// trabajador a una organización ajena, con solo conocer sus Ids.
/// </summary>
public class AutoridadEnAltasTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task No_se_puede_crear_un_centro_para_un_cliente_fuera_de_la_cartera()
    {
        Guid clienteId, empresaId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Ajeno Alta Centro S.A.", "B12345674", false, null, null);
            var empresa = new Empresa("Empresa Alta Centro S.L.", "B87654323");
            contexto.Empresas.AddRange(cliente, empresa);
            await contexto.SaveChangesAsync();
            clienteId = cliente.Id;
            empresaId = empresa.Id;
        }

        await using var contextoAlta = CrearContexto();
        var handler = new CrearCentroCommandHandler(
            new CentroRepository(contextoAlta), contextoAlta, contextoAlta,
            new TipoDocumentoCentroRepository(contextoAlta),
            new AlcanceDatosServiceFalso(clienteIds: [Guid.NewGuid()]), contextoAlta);

        var resultado = await handler.Handle(
            new CrearCentroCommand(clienteId, empresaId, "Centro Ajeno", null, null, null, null),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Centro.ClienteNoEncontrado");
        (await contextoAlta.Centros.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task No_se_puede_incorporar_un_trabajador_a_una_empresa_fuera_de_la_cartera()
    {
        Guid empresaId;
        await using (var contexto = CrearContexto())
        {
            var empresa = new Empresa("Empresa Ajena Alta Trabajador S.L.", "B87654323");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();
            empresaId = empresa.Id;
        }

        await using var contextoAlta = CrearContexto();
        var handler = new CrearTrabajadorCommandHandler(
            new TrabajadorRepository(contextoAlta), contextoAlta,
            new AlcanceDatosServiceFalso(empresaIds: [Guid.NewGuid()]), contextoAlta);

        var resultado = await handler.Handle(
            new CrearTrabajadorCommand(
                empresaId, null, "Secuestrado", "Trabajador", "77189989B", null, null, null),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Trabajador.EmpresaNoEncontrada");
        (await contextoAlta.Trabajadores.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task No_se_puede_crear_un_proyecto_para_un_cliente_fuera_de_la_cartera()
    {
        Guid clienteId, centroId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Ajeno Alta Proyecto S.A.", "B12345674", false, null, null);
            var empresa = new Empresa("Empresa Alta Proyecto S.L.", "B87654323");
            contexto.Empresas.AddRange(cliente, empresa);
            await contexto.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, "Centro Alta Proyecto");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();

            clienteId = cliente.Id;
            centroId = centro.Id;
        }

        await using var contextoAlta = CrearContexto();
        var handler = new CrearProyectoCommandHandler(
            new ProyectoRepository(contextoAlta), contextoAlta,
            new AlcanceDatosServiceFalso(clienteIds: [Guid.NewGuid()]), contextoAlta);

        var resultado = await handler.Handle(
            new CrearProyectoCommand(clienteId, centroId, "Proyecto Ajeno", new DateOnly(2026, 1, 1), null, null),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Proyecto.CentroNoEncontrado");
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
