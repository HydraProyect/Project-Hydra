using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
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

        var cliente = Empresa.CrearComoCliente("Concurrida S.L.", "B12345674", false, null, null);
        contexto.Empresas.Add(cliente);
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

        var clientePrimero = await contextoPrimero.Empresas.FirstAsync(c => c.Id == _clienteId);
        var clienteSegundo = await contextoSegundo.Empresas.FirstAsync(c => c.Id == _clienteId);

        clientePrimero.ActualizarComoCliente("Renombrada por el primero S.L.", "B12345674", esCritico: false, notas: null);
        await contextoPrimero.SaveChangesAsync();

        clienteSegundo.ActualizarComoCliente("Renombrada por el segundo S.L.", "B12345674", esCritico: true, notas: null);

        var guardarSegundo = async () => await contextoSegundo.SaveChangesAsync();

        await guardarSegundo.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "el segundo cargó una versión que ya no es la vigente");

        // Y lo que importa de verdad: el cambio del primero sigue ahí.
        await using var contextoComprobacion = CrearContexto();
        var almacenado = await contextoComprobacion.Empresas.FirstAsync(c => c.Id == _clienteId);
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
            var cliente = await contexto.Empresas.FirstAsync(c => c.Id == _clienteId);
            cliente.ActualizarComoCliente("Editada sin competencia S.L.", "B12345674", esCritico: true, notas: "ok");
            await contexto.SaveChangesAsync();
        }

        await using var contextoComprobacion = CrearContexto();
        var almacenado = await contextoComprobacion.Empresas.FirstAsync(c => c.Id == _clienteId);
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
            versionInicial = (await contexto.Empresas.FirstAsync(c => c.Id == _clienteId)).Version;
        }

        await using (var contexto = CrearContexto())
        {
            var cliente = await contexto.Empresas.FirstAsync(c => c.Id == _clienteId);
            cliente.ActualizarComoCliente("Otra razón social S.L.", "B12345674", esCritico: false, notas: null);
            await contexto.SaveChangesAsync();
        }

        await using var contextoComprobacion = CrearContexto();
        var versionFinal = (await contextoComprobacion.Empresas.FirstAsync(c => c.Id == _clienteId)).Version;

        versionFinal.Should().NotBe(versionInicial);
    }

    /// <summary>
    /// Auditoría módulo 6: dos refrescos concurrentes del mismo buzón de
    /// Microsoft 365 partían del mismo refresh token de Graph (que rota en
    /// cada canje) y se pisaban en silencio — el que ganara el guardado
    /// dejaba vigente un token que Graph ya había invalidado al emitir el
    /// otro, rompiendo la conexión hasta reconectarla a mano. Mismo
    /// escenario que arriba, pero sobre CredencialIntegracion en vez de
    /// Empresa: dos "respuestas" concurrentes (una manual, una de la
    /// ingesta de fondo) son exactamente dos DbContext independientes
    /// leyendo la misma fila.
    /// </summary>
    [Fact]
    public async Task Dos_refrescos_concurrentes_de_la_misma_credencial_no_se_pisan_en_silencio()
    {
        Guid credencialId;
        await using (var contexto = CrearContexto())
        {
            var conexion = new ConexionIntegracion("cae@cliente.test", "Buzón CAE");
            var credencial = new CredencialIntegracion(conexion.Id, "refresh-token-original");
            contexto.ConexionesIntegracion.Add(conexion);
            contexto.CredencialesIntegracion.Add(credencial);
            await contexto.SaveChangesAsync();
            credencialId = credencial.Id;
        }

        await using var contextoPrimero = CrearContexto();
        await using var contextoSegundo = CrearContexto();

        var credencialPrimero = await contextoPrimero.CredencialesIntegracion.FirstAsync(c => c.Id == credencialId);
        var credencialSegundo = await contextoSegundo.CredencialesIntegracion.FirstAsync(c => c.Id == credencialId);

        // Graph acepta las dos peticiones de refresco (el refresh token de
        // partida seguía siendo válido para ambas) y devuelve un par
        // distinto a cada una — el primero en guardar gana.
        credencialPrimero.ActualizarRefreshToken("refresh-token-rotado-por-el-primero");
        await contextoPrimero.SaveChangesAsync();

        credencialSegundo.ActualizarRefreshToken("refresh-token-rotado-por-el-segundo");
        var guardarSegundo = async () => await contextoSegundo.SaveChangesAsync();

        await guardarSegundo.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "el segundo cargó una versión de la credencial que ya no es la vigente");

        await using var contextoComprobacion = CrearContexto();
        var almacenada = await contextoComprobacion.CredencialesIntegracion.FirstAsync(c => c.Id == credencialId);
        almacenada.RefreshToken.Should().Be("refresh-token-rotado-por-el-primero");
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
