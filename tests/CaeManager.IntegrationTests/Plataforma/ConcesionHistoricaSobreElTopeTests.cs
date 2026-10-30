using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// <see cref="ConcesionPrivilegio.MaximoDiasDeVigencia"/> limita lo que se crea,
/// no lo que ya existe: una fila anterior al tope tiene que seguir leyéndose,
/// porque si la lectura la rechazara dejaría sin acceso a quien ya lo tiene. La
/// fila se escribe con SQL porque el dominio ya no permite construirla.
/// </summary>
public class ConcesionHistoricaSobreElTopeTests : IAsyncLifetime
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
    public async Task Una_fila_por_encima_del_tope_se_lee_sigue_cubriendo_y_se_puede_revocar()
    {
        var ahora = DateTime.UtcNow;
        var id = await SembrarFilaAsync(desde: ahora.AddDays(-1), hasta: ahora.AddDays(400));

        await using (var contexto = CrearContexto())
        {
            var concesion = await contexto.ConcesionesPrivilegio.SingleAsync(c => c.Id == id);

            (concesion.VigenciaHasta!.Value - concesion.VigenciaDesde).Should()
                .BeGreaterThan(TimeSpan.FromDays(ConcesionPrivilegio.MaximoDiasDeVigencia));
            concesion.CubreEn(_tenant, DateTime.UtcNow).Should().BeTrue();

            concesion.Revocar(DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        await using var relectura = CrearContexto();
        var guardada = await relectura.ConcesionesPrivilegio.SingleAsync(c => c.Id == id);
        guardada.Estado.Should().Be(EstadoConcesionPrivilegio.Revocada);
    }

    private async Task<Guid> SembrarFilaAsync(DateTime desde, DateTime hasta)
    {
        var id = Guid.NewGuid();

        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
INSERT INTO ""ConcesionesPrivilegio""
    (""Id"", ""UsuarioPlataformaId"", ""Capacidad"", ""EsAlcanceGlobal"",
     ""VigenciaDesde"", ""VigenciaHasta"", ""Estado"", ""Origen"", ""CreadoEnUtc"", ""Version"")
VALUES (@id, gen_random_uuid(), 'AdminPlataforma', true,
        @desde, @hasta, 'Vigente', 'Ordinaria', now(), gen_random_uuid());";
        comando.Parameters.AddWithValue("id", id);
        comando.Parameters.AddWithValue("desde", desde);
        comando.Parameters.AddWithValue("hasta", hasta);
        await comando.ExecuteNonQueryAsync();

        return id;
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
