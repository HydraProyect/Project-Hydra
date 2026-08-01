using CaeManager.Domain.Clientes;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// El repositorio no tenía ninguna forma de concurrencia optimista: dos
/// usuarios editando el mismo registro se pisaban en silencio y el primero no
/// se enteraba de que su cambio había desaparecido.
///
/// El escenario se reproduce con dos <c>DbContext</c> independientes sobre el
/// mismo archivo, que es exactamente lo que ocurre con dos circuitos de Blazor
/// abiertos a la vez.
/// </summary>
public class ConcurrenciaOptimistaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _clienteId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = new Cliente("Concurrida S.L.", "B12345674", esCritico: false);
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
    public async Task El_segundo_en_guardar_no_pisa_al_primero_en_silencio()
    {
        // Los dos leen el mismo registro, como dos gestores que abren la
        // misma ficha.
        await using var contextoPrimero = CrearContexto();
        await using var contextoSegundo = CrearContexto();

        var clientePrimero = await contextoPrimero.Clientes.FirstAsync(c => c.Id == _clienteId);
        var clienteSegundo = await contextoSegundo.Clientes.FirstAsync(c => c.Id == _clienteId);

        clientePrimero.Actualizar("Renombrada por el primero S.L.", "B12345674", esCritico: false, notas: null);
        await contextoPrimero.SaveChangesAsync();

        clienteSegundo.Actualizar("Renombrada por el segundo S.L.", "B12345674", esCritico: true, notas: null);

        var guardarSegundo = async () => await contextoSegundo.SaveChangesAsync();

        await guardarSegundo.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "el segundo cargó una versión que ya no es la vigente");

        // Y lo que importa de verdad: el cambio del primero sigue ahí.
        await using var contextoComprobacion = CrearContexto();
        var almacenado = await contextoComprobacion.Clientes.FirstAsync(c => c.Id == _clienteId);
        almacenado.RazonSocial.Should().Be("Renombrada por el primero S.L.");
        almacenado.EsCritico.Should().BeFalse();
    }

    [Fact]
    public async Task Una_edicion_sin_competencia_sigue_funcionando()
    {
        // Contrapeso imprescindible: el token no puede estorbar al caso
        // normal, que es el 99% de las ediciones.
        await using (var contexto = CrearContexto())
        {
            var cliente = await contexto.Clientes.FirstAsync(c => c.Id == _clienteId);
            cliente.Actualizar("Editada sin competencia S.L.", "B12345674", esCritico: true, notas: "ok");
            await contexto.SaveChangesAsync();
        }

        await using var contextoComprobacion = CrearContexto();
        var almacenado = await contextoComprobacion.Clientes.FirstAsync(c => c.Id == _clienteId);
        almacenado.RazonSocial.Should().Be("Editada sin competencia S.L.");
    }

    [Fact]
    public async Task Cada_modificacion_renueva_la_version()
    {
        // Si la versión no cambiara, el token existiría pero no detectaría
        // nada — el fallo clásico de la concurrencia optimista mal montada.
        Guid versionInicial;
        await using (var contexto = CrearContexto())
        {
            versionInicial = (await contexto.Clientes.FirstAsync(c => c.Id == _clienteId)).Version;
        }

        await using (var contexto = CrearContexto())
        {
            var cliente = await contexto.Clientes.FirstAsync(c => c.Id == _clienteId);
            cliente.Actualizar("Otra razón social S.L.", "B12345674", esCritico: false, notas: null);
            await contexto.SaveChangesAsync();
        }

        await using var contextoComprobacion = CrearContexto();
        var versionFinal = (await contextoComprobacion.Clientes.FirstAsync(c => c.Id == _clienteId)).Version;

        versionFinal.Should().NotBe(versionInicial);
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
