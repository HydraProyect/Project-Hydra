using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// P3-33: el refresh token OAuth y el ClientState del webhook (secreto que
/// permite confiar en una notificación sin sesión, ver
/// docs/MULTITENANCY.md § 8) tienen que salir cifrados a la base de datos —
/// mismo mecanismo que CredencialAccesoEmpresa. Contra Postgres real, no un
/// fake de IDataProtector: lo que hay que garantizar es la columna tal como
/// queda en disco.
/// </summary>
public class CifradoCredencialesIntegracionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly IDataProtectionProvider _dataProtectionProvider = new EphemeralDataProtectionProvider();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_refresh_token_no_se_guarda_en_claro_y_vuelve_intacto_al_leerlo()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        var credencial = new CredencialIntegracion(conexion.Id, "refresh-token-secreto");

        await using (var contexto = CrearContexto())
        {
            contexto.ConexionesIntegracion.Add(conexion);
            contexto.CredencialesIntegracion.Add(credencial);
            await contexto.SaveChangesAsync();
        }

        var columnaEnDisco = await LeerColumnaCruda("CredencialesIntegracion", "RefreshToken", credencial.Id);
        columnaEnDisco.Should().NotContain("refresh-token-secreto");

        await using var contextoLectura = CrearContexto();
        var recargada = await contextoLectura.CredencialesIntegracion.FirstAsync(c => c.Id == credencial.Id);
        recargada.RefreshToken.Should().Be("refresh-token-secreto");
    }

    [Fact]
    public async Task El_clientState_no_se_guarda_en_claro_y_vuelve_intacto_al_leerlo()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        var suscripcion = new SuscripcionWebhook(conexion.Id, "graph-sub-1", "client-state-secreto", DateTime.UtcNow.AddDays(3));

        await using (var contexto = CrearContexto())
        {
            contexto.ConexionesIntegracion.Add(conexion);
            contexto.SuscripcionesWebhook.Add(suscripcion);
            await contexto.SaveChangesAsync();
        }

        var columnaEnDisco = await LeerColumnaCruda("SuscripcionesWebhook", "ClientState", suscripcion.Id);
        columnaEnDisco.Should().NotContain("client-state-secreto");

        await using var contextoLectura = CrearContexto();
        var recargada = await contextoLectura.SuscripcionesWebhook.FirstAsync(s => s.Id == suscripcion.Id);
        recargada.ClientState.Should().Be("client-state-secreto");
    }

    private async Task<string> LeerColumnaCruda(string tabla, string columna, Guid id)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"SELECT \"{columna}\" FROM \"{tabla}\" WHERE \"Id\" = @id";
        comando.Parameters.AddWithValue("id", id);
        return (string)(await comando.ExecuteScalarAsync())!;
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, _dataProtectionProvider, tenantActual);
    }
}
