using CaeManager.Application.Clientes.Commands.EditarCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Domain.Clientes;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// El flujo real de edición de punta a punta, con el DbContext y el
/// interceptor tal como los registra la aplicación: la pantalla lee el
/// detalle (que trae <c>Version</c>), otra persona guarda, y la primera envía
/// su Command con la versión que tenía en pantalla.
///
/// Existe porque los otros dos tests de concurrencia, por separado, no
/// demuestran que la cadena entera funcione: uno prueba que EF detecta el
/// choque entre dos contextos abiertos a la vez, y otro que el handler
/// rechaza una versión distinta. Lo que se rompía en medio —el handler
/// recarga la entidad justo antes de escribir, así que el token de EF compara
/// la versión consigo misma— solo se ve juntándolo todo.
/// </summary>
public class EdicionConcurrenteExtremoAExtremoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _clienteId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = new Cliente("Ficha Compartida S.L.", "B12345674", esCritico: false);
        contexto.Clientes.Add(cliente);
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task La_segunda_persona_no_pisa_a_la_primera_y_recibe_un_mensaje()
    {
        // 1. Las dos abren la ficha: cada pantalla se queda con su Version.
        Guid versionQueVeA;
        Guid versionQueVeB;

        await using (var contexto = CrearContexto())
        {
            var detalle = await new ObtenerClientePorIdQueryHandler(contexto, new AlcanceDatosServiceFalso())
                .Handle(new ObtenerClientePorIdQuery(_clienteId), CancellationToken.None);
            versionQueVeA = detalle!.Version;
            versionQueVeB = detalle.Version;
        }

        // 2. A guarda.
        await using (var contexto = CrearContexto())
        {
            var resultado = await new EditarClienteCommandHandler(new ClienteRepository(contexto), contexto)
                .Handle(
                    new EditarClienteCommand(_clienteId, "Guardado por A S.L.", "B12345674", false, null, versionQueVeA),
                    CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        // 3. B guarda con la versión que tenía en pantalla, ya caducada.
        await using (var contexto = CrearContexto())
        {
            var resultado = await new EditarClienteCommandHandler(new ClienteRepository(contexto), contexto)
                .Handle(
                    new EditarClienteCommand(_clienteId, "Guardado por B S.L.", "B12345674", true, null, versionQueVeB),
                    CancellationToken.None);

            resultado.EsFallido.Should().BeTrue("B trae una versión que ya no es la vigente");
            resultado.Error.Codigo.Should().Be("Concurrencia.Conflicto");
        }

        // 4. Lo que quedó guardado es lo de A.
        await using var contextoComprobacion = CrearContexto();
        var almacenado = await contextoComprobacion.Clientes.FirstAsync(c => c.Id == _clienteId);
        almacenado.RazonSocial.Should().Be("Guardado por A S.L.");
        almacenado.EsCritico.Should().BeFalse();
    }

    [Fact]
    public async Task Al_recargar_la_ficha_la_segunda_persona_ya_puede_guardar()
    {
        // El conflicto tiene que ser recuperable: reabrir y volver a aplicar.
        await using (var contexto = CrearContexto())
        {
            var detalle = await new ObtenerClientePorIdQueryHandler(contexto, new AlcanceDatosServiceFalso())
                .Handle(new ObtenerClientePorIdQuery(_clienteId), CancellationToken.None);

            await new EditarClienteCommandHandler(new ClienteRepository(contexto), contexto)
                .Handle(
                    new EditarClienteCommand(_clienteId, "Primera edición S.L.", "B12345674", false, null, detalle!.Version),
                    CancellationToken.None);
        }

        await using (var contexto = CrearContexto())
        {
            var detalleRecargado = await new ObtenerClientePorIdQueryHandler(contexto, new AlcanceDatosServiceFalso())
                .Handle(new ObtenerClientePorIdQuery(_clienteId), CancellationToken.None);

            var resultado = await new EditarClienteCommandHandler(new ClienteRepository(contexto), contexto)
                .Handle(
                    new EditarClienteCommand(_clienteId, "Segunda edición S.L.", "B12345674", true, null, detalleRecargado!.Version),
                    CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        await using var contextoComprobacion = CrearContexto();
        (await contextoComprobacion.Clientes.FirstAsync(c => c.Id == _clienteId))
            .RazonSocial.Should().Be("Segunda edición S.L.");
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
