using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

public class TrazaPropiaDePurgasTests
{
    [Theory]
    [InlineData("IniciosSesion", "app_purgar_inicios_sesion()", "12 months")]
    [InlineData("SesionesUsuario", "app_purgar_sesiones_usuario()", "12 months")]
    [InlineData("RegistrosAuditoria", "app_purgar_registros_auditoria(false)", "5 years")]
    public async Task La_llamada_sql_registra_el_borrado_en_la_misma_transaccion(string purga, string funcion, string plazo)
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
        await propietario.OpenAsync();
        await using (var sembrar = new NpgsqlCommand("""
            INSERT INTO "RegistrosInicioSesion" ("Id", "UsuarioId", "OcurridoEnUtc", "Metodo", "Resultado", "Senales", "TenantId")
            VALUES (gen_random_uuid(), gen_random_uuid(), now() - interval '6 years', 'Contrasena', 'Correcto', 0, gen_random_uuid());
            INSERT INTO "SesionesUsuario" ("Id", "UsuarioId", "Sid", "EmitidaEnUtc", "TenantId")
            VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), now() - interval '6 years', gen_random_uuid());
            INSERT INTO "RegistrosAuditoria" ("Id", "FechaUtc", "EntidadId", "TenantId", "Accion", "EntidadTipo", "TipoActor")
            VALUES (gen_random_uuid(), now() - interval '6 years', gen_random_uuid(), gen_random_uuid(), 'Creado', 'Empresa', 'Sistema');
            """, propietario))
            await sembrar.ExecuteNonQueryAsync();

        await using var runtime = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(arnes.CadenaPropietario));
        await runtime.OpenAsync();
        await using (var transaccion = await runtime.BeginTransactionAsync())
        {
            await using var ejecutar = new NpgsqlCommand($"SELECT public.{funcion};", runtime, transaccion);
            ((int)(await ejecutar.ExecuteScalarAsync())!).Should().BeGreaterThan(0);
            await transaccion.RollbackAsync();
        }
        await using (var contar = new NpgsqlCommand("""SELECT count(*) FROM "TrazasPurga";""", propietario))
            ((long)(await contar.ExecuteScalarAsync())!).Should().Be(0, "el rollback revierte también la traza");

        int filas;
        await using (var ejecutar = new NpgsqlCommand($"SELECT public.{funcion};", runtime))
            filas = (int)(await ejecutar.ExecuteScalarAsync())!;
        filas.Should().BeGreaterThan(0);
        await using (var leer = new NpgsqlCommand($"""
            SELECT "Filas", "Rol", "OcurridaEnUtc" >= "CorteUtc",
                   abs(extract(epoch from ("CorteUtc" - (now() - interval '{plazo}')))) < 10,
                   abs(extract(epoch from (now() - "OcurridaEnUtc"))) < 10
            FROM "TrazasPurga" WHERE "Purga" = @purga;
            """, propietario))
        {
            leer.Parameters.AddWithValue("purga", purga);
            await using var lector = await leer.ExecuteReaderAsync();
            (await lector.ReadAsync()).Should().BeTrue("la llamada SQL debe dejar evidencia propia");
            lector.GetInt32(0).Should().Be(filas);
            lector.GetString(1).Should().Be("cae_app_runtime");
            lector.GetBoolean(2).Should().BeTrue();
            lector.GetBoolean(3).Should().BeTrue();
            lector.GetBoolean(4).Should().BeTrue("la traza fecha el borrado, no el corte");
            (await lector.ReadAsync()).Should().BeFalse();
        }
        await using (var repetir = new NpgsqlCommand($"SELECT public.{funcion};", runtime))
            ((int)(await repetir.ExecuteScalarAsync())!).Should().Be(0);
        await using (var contar = new NpgsqlCommand("""SELECT count(*) FROM "TrazasPurga";""", propietario))
            ((long)(await contar.ExecuteScalarAsync())!).Should().Be(1, "una pasada sin borrados no genera traza");
        await using var acceso = new NpgsqlCommand("""SELECT * FROM "TrazasPurga";""", runtime);
        var intento = () => acceso.ExecuteNonQueryAsync();
        (await intento.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    [Fact]
    public async Task Una_llamada_ajena_al_runtime_queda_como_otro()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
        await propietario.OpenAsync();
        await using (var sembrar = new NpgsqlCommand("""
            INSERT INTO "RegistrosInicioSesion" ("Id", "UsuarioId", "OcurridoEnUtc", "Metodo", "Resultado", "Senales", "TenantId")
            VALUES (gen_random_uuid(), gen_random_uuid(), now() - interval '6 years', 'Contrasena', 'Correcto', 0, gen_random_uuid());
            """, propietario))
            await sembrar.ExecuteNonQueryAsync();

        await using (var purgar = new NpgsqlCommand("SELECT public.app_purgar_inicios_sesion();", propietario))
            ((int)(await purgar.ExecuteScalarAsync())!).Should().BeGreaterThan(0);

        await using var leer = new NpgsqlCommand("""SELECT "Rol" FROM "TrazasPurga";""", propietario);
        ((string)(await leer.ExecuteScalarAsync())!).Should().Be("otro");
    }

    [Fact]
    public async Task Contar_sin_borrar_no_deja_traza()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
        await propietario.OpenAsync();
        await using (var sembrar = new NpgsqlCommand("""
            INSERT INTO "RegistrosAuditoria" ("Id", "FechaUtc", "EntidadId", "TenantId", "Accion", "EntidadTipo", "TipoActor")
            VALUES (gen_random_uuid(), now() - interval '6 years', gen_random_uuid(), gen_random_uuid(), 'Creado', 'Empresa', 'Sistema');
            """, propietario))
            await sembrar.ExecuteNonQueryAsync();

        await using var runtime = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(arnes.CadenaPropietario));
        await runtime.OpenAsync();
        await using (var simular = new NpgsqlCommand("SELECT public.app_purgar_registros_auditoria(true);", runtime))
            ((int)(await simular.ExecuteScalarAsync())!).Should().BeGreaterThan(0, "control positivo: hay filas caducadas que contar");

        await using var contar = new NpgsqlCommand("""SELECT count(*) FROM "TrazasPurga";""", propietario);
        ((long)(await contar.ExecuteScalarAsync())!).Should().Be(0, "la simulación no borra nada");
    }

    [Fact]
    public async Task El_propietario_de_cada_purga_escribe_la_traza_pese_a_la_rls_forzada()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        await using var conexion = new NpgsqlConnection(arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var consulta = new NpgsqlCommand("""
            SELECT count(*), count(*) FILTER (WHERE (r.rolsuper OR r.rolbypassrls)
                                                AND has_table_privilege(p.proowner, 'public."TrazasPurga"', 'INSERT'))
              FROM pg_proc p JOIN pg_roles r ON r.oid = p.proowner
             WHERE p.oid IN ('public.app_purgar_inicios_sesion()'::regprocedure,
                             'public.app_purgar_sesiones_usuario()'::regprocedure,
                             'public.app_purgar_registros_auditoria(boolean)'::regprocedure);
            """, conexion);
        await using var lector = await consulta.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        lector.GetInt64(0).Should().Be(3);
        lector.GetInt64(1).Should().Be(3, "sin esto el INSERT fallaría y arrastraría el borrado de la purga");
    }

    [Fact]
    public async Task La_traza_no_tiene_columnas_para_datos_personales()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        await using var conexion = new NpgsqlConnection(arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var consulta = new NpgsqlCommand("""
            SELECT string_agg(column_name, ',' ORDER BY ordinal_position) FROM information_schema.columns
             WHERE table_schema = 'public' AND table_name = 'TrazasPurga';
            """, conexion);
        ((string)(await consulta.ExecuteScalarAsync())!).Should().Be("Purga,OcurridaEnUtc,CorteUtc,Filas,Rol");
    }

    [Fact]
    public async Task La_tabla_tiene_rls_forzada_y_ningun_privilegio_para_roles_de_aplicacion()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        await using var conexion = new NpgsqlConnection(arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var consulta = new NpgsqlCommand("""
            SELECT relrowsecurity AND relforcerowsecurity,
                   NOT has_table_privilege('cae_app_runtime', 'public."TrazasPurga"', 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE'),
                   NOT has_table_privilege('cae_app_soporte', 'public."TrazasPurga"', 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE'),
                   NOT has_table_privilege('cae_app_aprovisionamiento', 'public."TrazasPurga"', 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE')
              FROM pg_class WHERE oid = 'public."TrazasPurga"'::regclass;
            """, conexion);
        await using var lector = await consulta.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        for (var i = 0; i < 4; i++) lector.GetBoolean(i).Should().BeTrue();
    }
}
