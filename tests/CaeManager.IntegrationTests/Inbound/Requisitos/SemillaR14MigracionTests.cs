using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Inbound.Requisitos;

/// <summary>
/// La semilla R-14 de <c>AnadePlantillasRequisitosInbound</c> no se omite en silencio ni pisa una fila ajena: si el
/// catálogo base ya tiene A1 o ETT con otro Id, o uno de los Id reservados con otro código o con otro estado, la migración
/// aborta y no queda aplicada. Así el Down tampoco puede borrar después una fila que no sembró.
/// </summary>
public class SemillaR14MigracionTests : IAsyncLifetime
{
    private const string MigracionAnterior = "20261115200000_AnadeOrigenDeOperacionExterna";
    private const string Migracion = "20261115210000_AnadePlantillasRequisitosInbound";
    private const string IdA1 = "b1400000-0000-4000-8000-0000000000a1";

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionDeBaseVacia();

    public async Task InitializeAsync()
    {
        await using var contexto = Contexto();
        await contexto.GetService<IMigrator>().MigrateAsync(MigracionAnterior);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    public static TheoryData<string, string, bool> FilasPrevias => new()
    {
        // Id reservado con otro código: el ON CONFLICT omitiría A1 y el Down borraría esta fila.
        { IdA1, "otro-requisito", false },
        // Id reservado con el código de A1 pero activo: contra R-14.
        { IdA1, "a1-trabajador-desplazado", true },
        // El código de A1 con otro Id.
        { "c1400000-0000-4000-8000-0000000000a1", "a1-trabajador-desplazado", false },
    };

    [Theory]
    [MemberData(nameof(FilasPrevias))]
    public async Task Una_fila_previa_que_choca_con_la_semilla_aborta_la_migracion(string id, string codigo, bool activo)
    {
        await using (var conexion = new NpgsqlConnection(_cadena))
        {
            await conexion.OpenAsync();
            await using var insertar = conexion.CreateCommand();
            insertar.CommandText = """
                INSERT INTO public."CatalogoRequisitosBase" ("Id", "Codigo", "Nombre", "Fundamento", "Nivel", "Activo")
                VALUES (@id, @codigo, 'Fila previa', 'Alta previa a la migración', 'N2', @activo);
                """;
            insertar.Parameters.AddWithValue("id", Guid.Parse(id));
            insertar.Parameters.AddWithValue("codigo", codigo);
            insertar.Parameters.AddWithValue("activo", activo);
            await insertar.ExecuteNonQueryAsync();
        }

        await using var contexto = Contexto();
        var migrar = () => contexto.GetService<IMigrator>().MigrateAsync(Migracion);

        (await migrar.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().StartWith("R-14:");

        (await contexto.Database.GetAppliedMigrationsAsync()).Should().NotContain(Migracion, "la migración no queda aplicada");
        await using var leer = new NpgsqlConnection(_cadena);
        await leer.OpenAsync();
        await using var fila = leer.CreateCommand();
        fila.CommandText = "SELECT \"Codigo\" FROM public.\"CatalogoRequisitosBase\" WHERE \"Id\" = @id;";
        fila.Parameters.AddWithValue("id", Guid.Parse(id));
        ((string?)await fila.ExecuteScalarAsync()).Should().Be(codigo, "la fila previa sigue como estaba");
    }

    private CaeManagerDbContext Contexto()
    {
        var tenant = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(
            _cadena, n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        return new(opciones.Options, new EphemeralDataProtectionProvider(), tenant);
    }
}
