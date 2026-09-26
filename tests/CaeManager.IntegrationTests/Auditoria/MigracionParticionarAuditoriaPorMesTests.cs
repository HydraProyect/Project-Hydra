using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Migrations.PostgreSQL;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// <b>La migración que particiona la auditoría no pierde ni altera ningún
/// evento</b> (P1-M2), ni al aplicarse ni al deshacerse.
///
/// <para>
/// Siembra por SQL sobre el esquema de la migración anterior (el modelo de EF
/// ya describe la PK compuesta, así que sembrar con el contexto no serviría),
/// con eventos de dos Tenants en meses con partición propia y en fechas que
/// caen en la partición por defecto: una anterior al límite de diez años y una
/// posterior al margen. La comparación es independiente de la comprobación que
/// hace la propia migración: la lista completa de filas, en JSON, antes y
/// después.
/// </para>
/// </summary>
public class MigracionParticionarAuditoriaPorMesTests : IAsyncLifetime
{
    private const string NombreMigracion = "ParticionarAuditoriaPorMes";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Particionar_y_deshacer_conservan_todos_los_eventos_indices_privilegios_y_politicas()
    {
        var (anterior, particionado) = await MigracionesAsync();
        await MigrarAsync(anterior);

        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        var ahora = DateTime.UtcNow;
        var fechas = new[]
        {
            new DateTime(2015, 3, 10, 8, 0, 0, DateTimeKind.Utc),
            ahora.AddMonths(-3),
            ahora,
            ahora.AddMonths(ParticionadoMensualEventos.MesesPorDelante - 1),
            ahora.AddYears(2),
        };
        foreach (var tenant in new[] { Guid.NewGuid(), Guid.NewGuid() })
            foreach (var fecha in fechas)
                await SembrarAsync(conexion, tenant, fecha);

        var antes = new Dictionary<string, Estado>();
        foreach (var (tabla, _) in ParticionadoMensualEventos.Tablas)
        {
            antes[tabla] = await LeerEstadoAsync(conexion, tabla);
            antes[tabla].Filas.Should().HaveCount(fechas.Length * 2, $"control positivo: la siembra de {tabla} está completa");
        }

        await MigrarAsync(particionado);
        await conexion.ReloadTypesAsync();

        foreach (var (tabla, columna) in ParticionadoMensualEventos.Tablas)
        {
            var despues = await LeerEstadoAsync(conexion, tabla);
            despues.Relkind.Should().Be("p");
            despues.Filas.Should().Equal(antes[tabla].Filas, $"ningún evento de {tabla} se pierde ni cambia al particionar");
            despues.ClavePrimaria.Should().Be($"Id,{columna}");
            despues.Indices.Should().BeEquivalentTo(antes[tabla].Indices, "los índices secundarios se recrean con el mismo nombre");
            despues.Privilegios.Should().BeEquivalentTo(antes[tabla].Privilegios, "los roles de aplicación conservan exactamente sus privilegios");
            despues.Politicas.Should().BeEquivalentTo(antes[tabla].Politicas);
            despues.RlsForzada.Should().BeTrue();

            (await ContarEnAsync(conexion, $"{tabla}{ParticionadoMensualEventos.SufijoDefecto}")).Should().Be(4,
                "la fecha anterior a diez años y la posterior al margen, de los dos Tenants, caen en la partición por defecto");
        }

        await ComprobarPoliticasAjenasAsync(conexion, "al particionar");

        await MigrarAsync(anterior);
        await conexion.ReloadTypesAsync();

        foreach (var (tabla, _) in ParticionadoMensualEventos.Tablas)
        {
            var deshecho = await LeerEstadoAsync(conexion, tabla);
            deshecho.Relkind.Should().Be("r");
            deshecho.Filas.Should().Equal(antes[tabla].Filas, $"deshacer tampoco pierde eventos de {tabla}");
            deshecho.ClavePrimaria.Should().Be("Id");
            deshecho.Indices.Should().BeEquivalentTo(antes[tabla].Indices);
            deshecho.Privilegios.Should().BeEquivalentTo(antes[tabla].Privilegios);
            deshecho.Politicas.Should().BeEquivalentTo(antes[tabla].Politicas);
            deshecho.RlsForzada.Should().BeTrue();
        }

        await ComprobarPoliticasAjenasAsync(conexion, "al deshacer");
    }

    /// <summary>
    /// La política de lectura de AspNetUsers (P1-M1) lee los dos registros con
    /// un EXISTS. Tras un RENAME seguiría el OID de la tabla apartada: tiene que
    /// nombrar la tabla viva, y nunca la apartada.
    /// </summary>
    private static async Task ComprobarPoliticasAjenasAsync(NpgsqlConnection conexion, string momento)
    {
        var expresiones = string.Join(" ", await ListaAsync(conexion,
            "SELECT coalesce(pg_get_expr(polqual, polrelid), '') || ' ' || coalesce(pg_get_expr(polwithcheck, polrelid), '') " +
            "FROM pg_policy WHERE polrelid = 'public.\"AspNetUsers\"'::regclass;"));

        foreach (var (tabla, _) in ParticionadoMensualEventos.Tablas)
            expresiones.Should().Contain($"\"{tabla}\"", $"control positivo: las políticas de AspNetUsers leen {tabla} ({momento})");
        expresiones.Should().NotContain("_previa").And.NotContain("_particionada",
            $"ninguna política puede quedar leyendo una tabla apartada ({momento})");
    }

    private sealed record Estado(
        string Relkind, List<string> Filas, string ClavePrimaria, List<string> Indices,
        List<string> Privilegios, List<string> Politicas, bool RlsForzada);

    private async Task<(string Anterior, string Particionado)> MigracionesAsync()
    {
        await using var contexto = CrearContexto();
        var todas = contexto.Database.GetMigrations().ToList();
        var indice = todas.FindIndex(m => m.EndsWith("_" + NombreMigracion, StringComparison.Ordinal));
        indice.Should().BeGreaterThan(0, $"la migración {NombreMigracion} tiene que existir y no ser la primera");
        return (todas[indice - 1], todas[indice]);
    }

    private async Task MigrarAsync(string migracion)
    {
        await using var contexto = CrearContexto();
        await contexto.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(migracion);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = null });
    }

    private static async Task SembrarAsync(NpgsqlConnection conexion, Guid tenant, DateTime fecha)
    {
        await using var orden = new NpgsqlCommand(
            """
            INSERT INTO "RegistrosAuditoria"
                ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "FechaUtc", "DatosAntes", "DatosDespues", "UsuarioId", "TipoActor")
            VALUES (@id, @tenant, 'Trabajador', @id, 'Modificado', @fecha, @antes, @despues, @usuario, 'Persona');
            INSERT INTO "RegistrosAccesoDocumentoSensible"
                ("Id", "TenantId", "DocumentoId", "OcurridoEnUtc", "Sensibilidad", "TipoAcceso", "ViaAcceso", "UsuarioId")
            VALUES (@id2, @tenant, @id, @fecha, 'Salud', 'Descarga', 'Directo', @usuario);
            """, conexion);
        orden.Parameters.AddWithValue("id", Guid.NewGuid());
        orden.Parameters.AddWithValue("id2", Guid.NewGuid());
        orden.Parameters.AddWithValue("tenant", tenant);
        orden.Parameters.AddWithValue("fecha", fecha);
        orden.Parameters.AddWithValue("usuario", Guid.NewGuid());
        // Comillas, barra invertida y acentos: el contenido tiene que llegar
        // byte a byte, no solo el recuento.
        orden.Parameters.AddWithValue("antes", """{"Nombre":"José \"Pepe\" Núñez","Ruta":"C:\\x"}""");
        orden.Parameters.AddWithValue("despues", """{"Nombre":"José Núñez"}""");
        await orden.ExecuteNonQueryAsync();
    }

    private static async Task<Estado> LeerEstadoAsync(NpgsqlConnection conexion, string tabla)
    {
        var relacion = $"public.\"{tabla}\"";
        return new Estado(
            await EscalarAsync<string>(conexion, $"SELECT relkind::text FROM pg_class WHERE oid = '{relacion}'::regclass;"),
            await ListaAsync(conexion, $"SELECT row_to_json(t)::text FROM {relacion} t ORDER BY \"Id\";"),
            await EscalarAsync<string>(conexion,
                $"SELECT string_agg(a.attname, ',' ORDER BY k.ord) FROM pg_index i " +
                $"CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord) " +
                $"JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum " +
                $"WHERE i.indrelid = '{relacion}'::regclass AND i.indisprimary;"),
            await ListaAsync(conexion,
                $"SELECT c.relname || ' ' || regexp_replace(pg_get_indexdef(i.indexrelid), '^.* USING ', '') FROM pg_index i " +
                $"JOIN pg_class c ON c.oid = i.indexrelid WHERE i.indrelid = '{relacion}'::regclass AND NOT i.indisprimary ORDER BY 1;"),
            await ListaAsync(conexion,
                $"SELECT rol || ' ' || privilegio FROM unnest(ARRAY['cae_app_runtime','cae_app_soporte','cae_app_aprovisionamiento']) rol, " +
                $"unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE','TRUNCATE']) privilegio " +
                $"WHERE has_table_privilege(rol, '{relacion}', privilegio) ORDER BY 1;"),
            await ListaAsync(conexion,
                $"SELECT polname || '|' || polcmd || '|' || polpermissive || '|' || coalesce(pg_get_expr(polqual, polrelid), '') " +
                $"|| '|' || coalesce(pg_get_expr(polwithcheck, polrelid), '') FROM pg_policy WHERE polrelid = '{relacion}'::regclass ORDER BY 1;"),
            await EscalarAsync<bool>(conexion,
                $"SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = '{relacion}'::regclass;"));
    }

    private static async Task<long> ContarEnAsync(NpgsqlConnection conexion, string particion) =>
        await EscalarAsync<long>(conexion, $"SELECT count(*) FROM public.\"{particion}\";");

    private static async Task<List<string>> ListaAsync(NpgsqlConnection conexion, string sql)
    {
        await using var orden = new NpgsqlCommand(sql, conexion);
        var lista = new List<string>();
        await using var lector = await orden.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            lista.Add(lector.GetString(0));
        return lista;
    }

    private static async Task<T> EscalarAsync<T>(NpgsqlConnection conexion, string sql)
    {
        await using var orden = new NpgsqlCommand(sql, conexion);
        return (T)(await orden.ExecuteScalarAsync())!;
    }
}
