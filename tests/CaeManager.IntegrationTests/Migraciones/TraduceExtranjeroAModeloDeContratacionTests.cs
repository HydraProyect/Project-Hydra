using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// La traducción de datos de <c>TraduceExtranjeroAModeloDeContratacion</c> en PostgreSQL de verdad: el SQL exacto de la
/// migración (<see cref="TraduceExtranjeroAModeloDeContratacion.TraduceMascaras"/> y
/// <see cref="TraduceExtranjeroAModeloDeContratacion.RevierteMascaras"/>) sobre una base propia con solo las columnas que lee
/// y escribe, para fijar fila a fila qué pasa con cada combinación de máscara, entidad y «Se pide a». Que la migración entera
/// se aplica sobre el esquema real lo prueba ya cualquier base migrada de la suite.
/// </summary>
public class TraduceExtranjeroAModeloDeContratacionTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionDeBaseVacia();

    public async Task InitializeAsync()
    {
        var mantenimiento = new NpgsqlConnectionStringBuilder(_cadena);
        var nombre = mantenimiento.Database!;
        mantenimiento.Database = "postgres";
        await using (var conexion = new NpgsqlConnection(mantenimiento.ConnectionString))
        {
            await conexion.OpenAsync();
            await using var crear = new NpgsqlCommand($"CREATE DATABASE \"{nombre}\";", conexion);
            await crear.ExecuteNonQueryAsync();
        }

        await EjecutarAsync("""
            CREATE TABLE public."TiposDocumento" ("Id" integer PRIMARY KEY, "TiposContratacionDeSerie" integer NOT NULL);
            CREATE TABLE public."RequisitosPlantilla" (
                "Id" integer PRIMARY KEY, "Entidad" text NOT NULL, "SePideA" text NOT NULL, "TiposContratacion" integer NOT NULL);
            """);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task Los_tipos_de_documento_de_serie_a_todos_pasan_a_15_y_las_demas_mascaras_no_cambian()
    {
        // 7 = el Todos anterior; 3 = cuenta ajena y autónomo; 4 = solo voluntario.
        await EjecutarAsync("""INSERT INTO public."TiposDocumento" VALUES (1, 7), (2, 3), (3, 4);""");

        await EjecutarAsync(TraduceExtranjeroAModeloDeContratacion.TraduceMascaras);

        (await LeerAsync("""SELECT "Id", "TiposContratacionDeSerie" FROM public."TiposDocumento" ORDER BY "Id";"""))
            .Should().Equal((1, 15), (2, 3), (3, 4));
    }

    [Fact]
    public async Task Los_requisitos_de_Trabajador_traducen_Se_pide_a_a_la_mascara()
    {
        await EjecutarAsync("""
            INSERT INTO public."RequisitosPlantilla" VALUES
                (1, 'Trabajador', 'Extranjeros', 7),
                (2, 'Trabajador', 'Extranjeros', 3),
                (3, 'Trabajador', 'Nacionales', 7),
                (4, 'Trabajador', 'Nacionales', 1),
                (5, 'Trabajador', 'Todos', 7),
                (6, 'Trabajador', 'Todos', 3);
            """);

        await EjecutarAsync(TraduceExtranjeroAModeloDeContratacion.TraduceMascaras);

        (await LeerAsync("""SELECT "Id", "TiposContratacion" FROM public."RequisitosPlantilla" ORDER BY "Id";"""))
            .Should().Equal((1, 8), (2, 8), (3, 7), (4, 1), (5, 15), (6, 3));
        (await LeerTextoAsync("""SELECT string_agg("SePideA", ',' ORDER BY "Id") FROM public."RequisitosPlantilla";"""))
            .Should().Be("Extranjeros,Extranjeros,Nacionales,Nacionales,Todos,Todos", "«Se pide a» no se reescribe");
    }

    [Fact]
    public async Task Un_requisito_de_Empresa_o_de_vehiculo_queda_intacto_mascara_y_Se_pide_a()
    {
        // Decisión de la subcoordinadora de 2026-11-17: la del Extranjero solo trata del Trabajador; una Empresa contratista
        // pedida solo a extranjeras (2026-11-14) sigue así.
        await EjecutarAsync("""
            INSERT INTO public."RequisitosPlantilla" VALUES
                (1, 'Empresa', 'Extranjeros', 7),
                (2, 'Empresa', 'Nacionales', 7),
                (3, 'VehiculoOMaquina', 'Todos', 7),
                (4, 'Empresa', 'Todos', 7);
            """);

        await EjecutarAsync(TraduceExtranjeroAModeloDeContratacion.TraduceMascaras);

        (await LeerAsync("""SELECT "Id", "TiposContratacion" FROM public."RequisitosPlantilla" ORDER BY "Id";"""))
            .Should().Equal((1, 7), (2, 7), (3, 7), (4, 7));
        (await LeerTextoAsync("""SELECT string_agg("SePideA", ',' ORDER BY "Id") FROM public."RequisitosPlantilla";"""))
            .Should().Be("Extranjeros,Nacionales,Todos,Todos");
    }

    [Fact]
    public async Task El_Down_devuelve_las_mascaras_anteriores_con_perdida_declarada()
    {
        await EjecutarAsync("""
            INSERT INTO public."TiposDocumento" VALUES (1, 7), (2, 3), (3, 9), (4, 8);
            INSERT INTO public."RequisitosPlantilla" VALUES
                (1, 'Trabajador', 'Extranjeros', 7),
                (2, 'Trabajador', 'Nacionales', 7),
                (3, 'Trabajador', 'Todos', 7),
                (4, 'Empresa', 'Nacionales', 7),
                (5, 'Trabajador', 'Todos', 9);
            """);

        await EjecutarAsync(TraduceExtranjeroAModeloDeContratacion.TraduceMascaras);
        await EjecutarAsync(TraduceExtranjeroAModeloDeContratacion.RevierteMascaras);

        // Tipo 4 (solo Extranjero, escrito después de subir) y tipo 3 (cuenta ajena y Extranjero) pierden el bit: la pérdida declarada.
        (await LeerAsync("""SELECT "Id", "TiposContratacionDeSerie" FROM public."TiposDocumento" ORDER BY "Id";"""))
            .Should().Equal((1, 7), (2, 3), (3, 1), (4, 7));
        // El requisito 1 vuelve a 7 con «Extranjeros» intacto, que le devuelve el sentido anterior.
        (await LeerAsync("""SELECT "Id", "TiposContratacion" FROM public."RequisitosPlantilla" ORDER BY "Id";"""))
            .Should().Equal((1, 7), (2, 7), (3, 7), (4, 7), (5, 1));
    }

    private async Task EjecutarAsync(string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        await comando.ExecuteNonQueryAsync();
    }

    private async Task<List<(int, int)>> LeerAsync(string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        await using var lector = await comando.ExecuteReaderAsync();
        var filas = new List<(int, int)>();
        while (await lector.ReadAsync())
            filas.Add((lector.GetInt32(0), lector.GetInt32(1)));
        return filas;
    }

    private async Task<string?> LeerTextoAsync(string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        return (string?)await comando.ExecuteScalarAsync();
    }
}
