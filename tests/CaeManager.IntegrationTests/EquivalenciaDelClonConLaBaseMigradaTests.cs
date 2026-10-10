using FluentAssertions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// La propiedad de la que depende toda la suite desde que cada test recibe un
/// clon de la plantilla migrada (<see cref="PlantillaMigradaDePruebas"/>) en vez
/// de migrar su propia base: <b>un clon es indistinguible de una base creada y
/// migrada como antes</b>. Si deja de serlo, los tests de aislamiento y de RLS
/// estarían midiendo otra base que la que produce el migrador.
///
/// <para>
/// Se compara la huella de catálogos de las dos (<see cref="HuellaDeBaseDeDatos"/>),
/// que incluye el texto de cada política RLS, <c>FORCE ROW LEVEL SECURITY</c>
/// tabla por tabla, propietarios, permisos y —lo que <c>CREATE DATABASE …
/// TEMPLATE</c> no copia— los permisos sobre la base y los ajustes por base y por
/// rol. El día que una migración añada un <c>ALTER DATABASE … SET</c>, un
/// <c>ALTER ROLE … IN DATABASE … SET</c> o un <c>GRANT … ON DATABASE</c>, la base
/// migrada lo tendrá, el clon no, y el primer test de esta clase caerá en rojo
/// nombrándolo: entonces hay que hacer que el clonado lo reproduzca.
/// </para>
///
/// <para>
/// El resto de la clase fija la sensibilidad del instrumento: cada alteración de
/// la lista, hecha a mano sobre una base, tiene que aparecer como diferencia.
/// Una huella que no viera una política borrada daría por equivalentes dos bases
/// que no lo son.
/// </para>
/// </summary>
public class EquivalenciaDelClonConLaBaseMigradaTests
    : IClassFixture<EquivalenciaDelClonConLaBaseMigradaTests.LasDosBases>
{
    private readonly LasDosBases _bases;

    public EquivalenciaDelClonConLaBaseMigradaTests(LasDosBases bases) => _bases = bases;

    [Fact]
    public void Un_clon_de_la_plantilla_es_igual_a_una_base_creada_y_migrada_como_antes()
    {
        // El instrumento ve algo en cada categoría que el esquema tiene: una
        // consulta que devolviera cero filas en las dos bases «coincidiría».
        foreach (var categoria in CategoriasQueElEsquemaTiene)
            _bases.HuellaMigrada.Keys.Should().Contain(
                clave => clave.StartsWith($"[{categoria}] ", StringComparison.Ordinal),
                $"la huella debe leer la categoría «{categoria}» de una base migrada");

        var diferencias = HuellaDeBaseDeDatos.Diferencias(
            _bases.HuellaMigrada, "la base migrada", _bases.HuellaDelClon, "el clon");

        diferencias.Should().BeEmpty(
            "un clon de la plantilla debe ser idéntico a una base migrada de verdad; si la diferencia es de nivel de "
            + "base (permisos o ajustes), clonar con TEMPLATE no la copia y el clonado tiene que reproducirla");
    }

    [Fact]
    public async Task La_identidad_efectiva_de_PostgreSQL_es_la_misma_en_el_clon_que_en_la_base_migrada()
    {
        var enLaMigrada = await IdentidadEfectivaAsync(_bases.CadenaMigrada);
        var enElClon = await IdentidadEfectivaAsync(_bases.CadenaDelClon);

        enElClon.Should().BeEquivalentTo(enLaMigrada);
        enElClon.ComoRuntime.Should().Be("current_user=cae_app_runtime session_user=cae_app_runtime superusuario=f bypassrls=f conecta=t",
            "los tests bajo runtime entran con LOGIN real como el rol de tráfico, también en un clon");
        enElClon.PropietariosDeLasTablas.Should().NotBeEmpty().And.NotContain("cae_app_runtime",
            "el rol de tráfico no es dueño de ninguna tabla: si lo fuera, FORCE ROW LEVEL SECURITY sería lo único que lo sujeta");
    }

    [Fact]
    public async Task Cada_clon_es_una_base_distinta_y_lo_que_se_escribe_en_uno_no_llega_a_otro_ni_a_la_plantilla()
    {
        var primero = BaseDatosPostgresDePruebas.CadenaConexionUnica();
        var segundo = BaseDatosPostgresDePruebas.CadenaConexionUnica();
        string? posterior = null;
        try
        {
            await EjecutarAsync(primero, "CREATE TABLE public.marca_de_un_solo_test (id integer PRIMARY KEY);");
            await EjecutarAsync(primero, "INSERT INTO public.marca_de_un_solo_test VALUES (1);");

            // Un clon hecho DESPUÉS de escribir: si la escritura hubiera llegado
            // a la plantilla, este la heredaría.
            posterior = BaseDatosPostgresDePruebas.CadenaConexionUnica();

            (await EscalarAsync(primero, "SELECT to_regclass('public.marca_de_un_solo_test') IS NOT NULL;")).Should().Be(true);
            (await EscalarAsync(segundo, "SELECT to_regclass('public.marca_de_un_solo_test') IS NOT NULL;")).Should().Be(false);
            (await EscalarAsync(posterior, "SELECT to_regclass('public.marca_de_un_solo_test') IS NOT NULL;")).Should().Be(false);

            var identificadores = new[]
            {
                await EscalarAsync(primero, "SELECT oid::bigint FROM pg_database WHERE datname = current_database();"),
                await EscalarAsync(segundo, "SELECT oid::bigint FROM pg_database WHERE datname = current_database();"),
                await EscalarAsync(posterior, "SELECT oid::bigint FROM pg_database WHERE datname = current_database();"),
            };
            identificadores.Should().OnlyHaveUniqueItems("cada test recibe una base propia, no una conexión a una compartida");
        }
        finally
        {
            await BaseDatosPostgresDePruebas.EliminarAsync(primero);
            await BaseDatosPostgresDePruebas.EliminarAsync(segundo);
            if (posterior is not null)
                await BaseDatosPostgresDePruebas.EliminarAsync(posterior);
        }
    }

    [Fact]
    public async Task La_plantilla_no_admite_conexiones_ni_del_superusuario()
    {
        string plantilla;
        await using (var mantenimiento = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool()))
        {
            await mantenimiento.OpenAsync();
            plantilla = PlantillaMigradaDePruebas.DeLaSuite.Asegurar(mantenimiento);
        }

        var cadenaALaPlantilla =
            new NpgsqlConnectionStringBuilder(BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool())
            {
                Database = plantilla,
            }.ConnectionString;

        var conectar = async () =>
        {
            await using var conexion = new NpgsqlConnection(cadenaALaPlantilla);
            await conexion.OpenAsync();
        };

        (await conectar.Should().ThrowAsync<PostgresException>(
                "sin conexiones nadie puede escribir en la plantilla: es lo que la mantiene igual de un test al siguiente"))
            .Which.SqlState.Should().Be("55000");
    }

    /// <summary>
    /// Alteraciones de esquema y de seguridad, hechas sobre un clon y comparadas
    /// con la base migrada. Los objetos se eligen leyendo el catálogo, no por
    /// nombre: la lista no envejece cuando cambian las tablas.
    /// </summary>
    private static readonly Alteraciones AlteracionesDelEsquema = new()
    {
        {
            "borrar una politica RLS",
            """
            DO $$ DECLARE p record; BEGIN
              SELECT schemaname, tablename, policyname INTO STRICT p FROM pg_policies ORDER BY 1, 2, 3 LIMIT 1;
              EXECUTE format('DROP POLICY %I ON %I.%I', p.policyname, p.schemaname, p.tablename);
            END $$;
            """,
            "[política RLS] "
        },
        {
            "cambiar el USING de una politica RLS",
            """
            DO $$ DECLARE p record; BEGIN
              SELECT schemaname, tablename, policyname INTO STRICT p FROM pg_policies
              WHERE qual IS NOT NULL ORDER BY 1, 2, 3 LIMIT 1;
              EXECUTE format('ALTER POLICY %I ON %I.%I USING (true)', p.policyname, p.schemaname, p.tablename);
            END $$;
            """,
            "[política RLS] "
        },
        {
            "cambiar el WITH CHECK de una politica RLS",
            """
            DO $$ DECLARE p record; BEGIN
              SELECT schemaname, tablename, policyname INTO STRICT p FROM pg_policies
              WHERE with_check IS NOT NULL ORDER BY 1, 2, 3 LIMIT 1;
              EXECUTE format('ALTER POLICY %I ON %I.%I WITH CHECK (true)', p.policyname, p.schemaname, p.tablename);
            END $$;
            """,
            "[política RLS] "
        },
        {
            "cambiar los roles de una politica RLS",
            """
            DO $$ DECLARE p record; BEGIN
              SELECT schemaname, tablename, policyname INTO STRICT p FROM pg_policies
              WHERE NOT ('cae_app_soporte' = ANY (roles)) ORDER BY 1, 2, 3 LIMIT 1;
              EXECUTE format('ALTER POLICY %I ON %I.%I TO cae_app_soporte', p.policyname, p.schemaname, p.tablename);
            END $$;
            """,
            "[política RLS] "
        },
        {
            "quitar FORCE ROW LEVEL SECURITY de una tabla",
            """
            DO $$ DECLARE t regclass; BEGIN
              SELECT c.oid::regclass INTO STRICT t FROM pg_class c
              WHERE c.relkind = 'r' AND c.relforcerowsecurity AND c.relnamespace = 'public'::regnamespace
              ORDER BY c.relname LIMIT 1;
              EXECUTE format('ALTER TABLE %s NO FORCE ROW LEVEL SECURITY', t);
            END $$;
            """,
            "[RLS de la tabla] "
        },
        {
            "deshabilitar RLS en una tabla",
            """
            DO $$ DECLARE t regclass; BEGIN
              SELECT c.oid::regclass INTO STRICT t FROM pg_class c
              WHERE c.relkind = 'r' AND c.relrowsecurity AND c.relnamespace = 'public'::regnamespace
              ORDER BY c.relname LIMIT 1;
              EXECUTE format('ALTER TABLE %s DISABLE ROW LEVEL SECURITY', t);
            END $$;
            """,
            "[RLS de la tabla] "
        },
        {
            "revocar un permiso de tabla al rol de trafico",
            """
            DO $$ DECLARE t regclass; BEGIN
              SELECT c.oid::regclass INTO STRICT t FROM pg_class c, LATERAL aclexplode(c.relacl) x
              WHERE c.relkind = 'r' AND c.relnamespace = 'public'::regnamespace
                AND x.grantee = 'cae_app_runtime'::regrole AND x.privilege_type = 'SELECT'
              ORDER BY c.relname LIMIT 1;
              EXECUTE format('REVOKE SELECT ON %s FROM cae_app_runtime', t);
            END $$;
            """,
            "[permiso sobre la tabla] "
        },
        {
            "conceder un permiso de tabla de mas",
            """
            DO $$ DECLARE t regclass; BEGIN
              SELECT c.oid::regclass INTO STRICT t FROM pg_class c
              WHERE c.relkind = 'r' AND c.relnamespace = 'public'::regnamespace
                AND NOT has_table_privilege('cae_app_soporte', c.oid, 'DELETE')
              ORDER BY c.relname LIMIT 1;
              EXECUTE format('GRANT DELETE ON %s TO cae_app_soporte', t);
            END $$;
            """,
            "[permiso sobre la tabla] "
        },
        {
            "cambiar el propietario de una tabla",
            """
            DO $$ DECLARE t regclass; BEGIN
              SELECT c.oid::regclass INTO STRICT t FROM pg_class c
              WHERE c.relkind = 'r' AND c.relnamespace = 'public'::regnamespace AND NOT c.relispartition
              ORDER BY c.relname LIMIT 1;
              EXECUTE format('ALTER TABLE %s OWNER TO cae_app_runtime', t);
            END $$;
            """,
            "[relación] "
        },
        {
            "cambiar los privilegios por defecto",
            "ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE SELECT ON TABLES FROM cae_app_soporte;",
            "[privilegios por defecto] "
        },
        {
            "anadir una columna",
            """ALTER TABLE "__EFMigrationsHistory" ADD COLUMN columna_de_mas integer;""",
            "[columna] "
        },
        {
            "quitar el valor por defecto de una columna",
            """
            DO $$ DECLARE d record; BEGIN
              SELECT a.attrelid::regclass AS tabla, a.attname INTO STRICT d
              FROM pg_attrdef f JOIN pg_attribute a ON a.attrelid = f.adrelid AND a.attnum = f.adnum
                   JOIN pg_class c ON c.oid = a.attrelid
              WHERE c.relkind = 'r' AND c.relnamespace = 'public'::regnamespace AND a.attgenerated = ''
                AND NOT c.relispartition
              ORDER BY c.relname, a.attnum LIMIT 1;
              EXECUTE format('ALTER TABLE %s ALTER COLUMN %I DROP DEFAULT', d.tabla, d.attname);
            END $$;
            """,
            "[columna] "
        },
        {
            "borrar un indice",
            """
            DO $$ DECLARE i regclass; BEGIN
              SELECT x.indexrelid::regclass INTO STRICT i FROM pg_index x JOIN pg_class c ON c.oid = x.indexrelid
              WHERE c.relnamespace = 'public'::regnamespace
                AND NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = x.indexrelid)
                AND NOT EXISTS (SELECT 1 FROM pg_inherits h WHERE h.inhrelid = x.indexrelid)
              ORDER BY c.relname LIMIT 1;
              EXECUTE format('DROP INDEX %s', i);
            END $$;
            """,
            "[índice] "
        },
        {
            "borrar una restriccion CHECK",
            """
            DO $$ DECLARE k record; BEGIN
              SELECT conrelid::regclass AS tabla, conname INTO STRICT k FROM pg_constraint
              WHERE contype = 'c' AND connamespace = 'public'::regnamespace AND conparentid = 0 AND conislocal
                AND conrelid <> 0 AND NOT (SELECT relispartition FROM pg_class WHERE oid = conrelid)
              ORDER BY conrelid::regclass::text, conname LIMIT 1;
              EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', k.tabla, k.conname);
            END $$;
            """,
            "[restricción] "
        },
        {
            "quitar SECURITY DEFINER a una funcion",
            """
            DO $$ DECLARE f regprocedure; BEGIN
              SELECT p.oid::regprocedure INTO STRICT f FROM pg_proc p
              WHERE p.pronamespace IN ('public'::regnamespace, 'app_privado'::regnamespace)
                AND p.prolang = (SELECT oid FROM pg_language WHERE lanname = 'plpgsql') AND p.prosecdef
              ORDER BY p.proname LIMIT 1;
              EXECUTE format('ALTER FUNCTION %s SECURITY INVOKER', f);
            END $$;
            """,
            "[función] "
        },
        {
            "deshabilitar un disparador",
            """
            DO $$ DECLARE t record; BEGIN
              SELECT g.tgrelid::regclass AS tabla, g.tgname INTO STRICT t FROM pg_trigger g JOIN pg_class c ON c.oid = g.tgrelid
              WHERE NOT g.tgisinternal AND g.tgparentid = 0 AND c.relnamespace = 'public'::regnamespace
              ORDER BY c.relname, g.tgname LIMIT 1;
              EXECUTE format('ALTER TABLE %s DISABLE TRIGGER %I', t.tabla, t.tgname);
            END $$;
            """,
            "[disparador] "
        },
        {
            "borrar una fila del historial de migraciones",
            """
            DELETE FROM "__EFMigrationsHistory"
            WHERE "MigrationId" = (SELECT max("MigrationId") FROM "__EFMigrationsHistory");
            """,
            "[historial de migraciones] "
        },
        {
            "dejar una fila de mas en una tabla",
            """INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('99999999999999_Inventada', '0');""",
            "[contenido de la tabla] "
        },
        {
            "crear una secuencia",
            "CREATE SEQUENCE public.secuencia_de_mas START 7 OWNED BY NONE;",
            "[secuencia] public.secuencia_de_mas"
        },
        {
            "borrar una extension",
            "DROP EXTENSION pg_trgm CASCADE;",
            "[extensión] pg_trgm"
        },
        {
            "crear un objeto de una clase que la huella no nombra (un operador)",
            "CREATE OPERATOR public.=== (LEFTARG = integer, RIGHTARG = integer, FUNCTION = int4eq);",
            "[recuento del catálogo] pg_operator"
        },
    };

    [Theory]
    [MemberData(nameof(NombresDeLasAlteracionesDelEsquema))]
    public async Task La_huella_delata_una_alteracion_del_esquema_o_de_la_seguridad(string alteracion)
    {
        var (sql, diferenciaEsperada) = AlteracionesDelEsquema[alteracion];
        var clon = BaseDatosPostgresDePruebas.CadenaConexionUnica();
        try
        {
            await EjecutarAsync(clon, sql);

            var diferencias = HuellaDeBaseDeDatos.Diferencias(
                _bases.HuellaMigrada, "la base migrada", await HuellaDeBaseDeDatos.CalcularAsync(clon), "el clon alterado");

            diferencias.Should().Contain(
                diferencia => diferencia.StartsWith(diferenciaEsperada, StringComparison.Ordinal),
                $"tras «{alteracion}» el clon ya no equivale a la base migrada y la huella tiene que decirlo");
        }
        finally
        {
            await BaseDatosPostgresDePruebas.EliminarAsync(clon);
        }
    }

    /// <summary>
    /// Lo que <c>CREATE DATABASE … TEMPLATE</c> no copia. La alteración se hace
    /// sobre una base <b>creada y migrada como antes</b> —es donde la dejaría una
    /// migración— y se compara con el clon, que no la tiene.
    /// </summary>
    private static readonly Alteraciones AlteracionesDeNivelDeBase = new()
    {
        {
            "ALTER DATABASE SET",
            "ALTER DATABASE {BASE} SET statement_timeout = '7s';",
            "[ajuste de base o de rol en la base] (todos los roles)"
        },
        {
            "ALTER ROLE IN DATABASE SET",
            "ALTER ROLE cae_app_runtime IN DATABASE {BASE} SET statement_timeout = '7s';",
            "[ajuste de base o de rol en la base] cae_app_runtime"
        },
        {
            "GRANT ON DATABASE",
            "GRANT CREATE ON DATABASE {BASE} TO cae_app_runtime;",
            "[privilegio efectivo sobre la base] cae_app_runtime · CREATE"
        },
        {
            "REVOKE ON DATABASE FROM PUBLIC",
            "REVOKE CONNECT ON DATABASE {BASE} FROM PUBLIC;",
            "[privilegio efectivo sobre la base] public · CONNECT"
        },
        {
            "ALTER DATABASE OWNER TO",
            "ALTER DATABASE {BASE} OWNER TO cae_app_runtime;",
            "[base de datos] atributos"
        },
        {
            "ALTER DATABASE CONNECTION LIMIT",
            "ALTER DATABASE {BASE} CONNECTION LIMIT 5;",
            "[base de datos] atributos"
        },
        {
            "COMMENT ON DATABASE",
            "COMMENT ON DATABASE {BASE} IS 'comentario que el clon no hereda';",
            "[comentario de la base] comentario"
        },
    };

    [Theory]
    [MemberData(nameof(NombresDeLasAlteracionesDeNivelDeBase))]
    public async Task La_huella_delata_lo_que_TEMPLATE_no_copia_si_una_base_migrada_lo_tuviera(string alteracion)
    {
        var (sql, diferenciaEsperada) = AlteracionesDeNivelDeBase[alteracion];
        var migrada = BaseDatosPostgresDePruebas.CadenaConexionDeBaseVacia();
        try
        {
            await BaseDatosPostgresDePruebas.MigrarAsync(migrada);
            var nombre = new NpgsqlConnectionStringBuilder(migrada).Database;
            await EjecutarAsync(migrada, sql.Replace("{BASE}", $"\"{nombre}\"", StringComparison.Ordinal));

            var diferencias = HuellaDeBaseDeDatos.Diferencias(
                await HuellaDeBaseDeDatos.CalcularAsync(migrada), "la base migrada con el ajuste", _bases.HuellaDelClon, "el clon");

            diferencias.Should().Contain(
                diferencia => diferencia.StartsWith(diferenciaEsperada, StringComparison.Ordinal),
                $"si una migración hiciera «{alteracion}», el clon no lo heredaría y la huella tiene que decirlo");

            // Y nada más: cada caso es además otra base migrada de verdad que,
            // quitando lo que se le acaba de hacer, coincide con el clon.
            diferencias.Where(diferencia => !CategoriasDeNivelDeBase.Any(
                    categoria => diferencia.StartsWith($"[{categoria}] ", StringComparison.Ordinal)))
                .Should().BeEmpty("la alteración solo toca el nivel de base");
        }
        finally
        {
            await BaseDatosPostgresDePruebas.EliminarAsync(migrada);
        }
    }

    // Los casos viajan por nombre: el SQL en el nombre del test lo partiría en varias líneas.
    public static TheoryData<string> NombresDeLasAlteracionesDelEsquema() => Nombres(AlteracionesDelEsquema);

    public static TheoryData<string> NombresDeLasAlteracionesDeNivelDeBase() => Nombres(AlteracionesDeNivelDeBase);

    private static TheoryData<string> Nombres(Alteraciones alteraciones)
    {
        var nombres = new TheoryData<string>();
        foreach (var nombre in alteraciones.Keys)
            nombres.Add(nombre);

        return nombres;
    }

    /// <summary>Nombre de la alteración → el SQL que la hace y la diferencia que debe aparecer.</summary>
    private sealed class Alteraciones : Dictionary<string, (string Sql, string Diferencia)>
    {
        public void Add(string nombre, string sql, string diferencia) => Add(nombre, (sql, diferencia));
    }

    private static readonly string[] CategoriasDeNivelDeBase =
    [
        "base de datos", "privilegio efectivo sobre la base", "ajuste de base o de rol en la base",
        "comentario de la base", "etiqueta de seguridad de la base",
    ];

    private static readonly string[] CategoriasQueElEsquemaTiene =
    [
        "esquema", "relación", "RLS de la tabla", "política RLS", "permiso sobre la tabla", "privilegios por defecto",
        "columna", "índice", "restricción", "función", "disparador", "disparador interno", "tipo", "extensión",
        "dependencia de rol", "historial de migraciones", "contenido de la tabla", "base de datos",
        "privilegio efectivo sobre la base", "recuento del catálogo",
    ];

    private sealed record Identidad(string ComoPropietario, string ComoRuntime, string PropietariosDeLasTablas);

    private static async Task<Identidad> IdentidadEfectivaAsync(string cadenaDelPropietario)
    {
        const string QuienSoy = """
            SELECT format('current_user=%s session_user=%s superusuario=%s bypassrls=%s conecta=%s',
                          current_user, session_user, r.rolsuper, r.rolbypassrls,
                          has_database_privilege(current_user, current_database(), 'CONNECT'))
            FROM pg_roles r WHERE r.rolname = current_user;
            """;
        const string Propietarios = """
            SELECT string_agg(DISTINCT pg_get_userbyid(c.relowner), ', ')
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p') AND n.nspname IN ('public', 'app_privado');
            """;

        return new Identidad(
            (string)(await EscalarAsync(cadenaDelPropietario, QuienSoy))!,
            (string)(await EscalarAsync(BaseDatosPostgresDePruebas.CadenaComoRuntime(cadenaDelPropietario), QuienSoy))!,
            (string)(await EscalarAsync(cadenaDelPropietario, Propietarios))!);
    }

    private static async Task EjecutarAsync(string cadena, string sql)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<object?> EscalarAsync(string cadena, string sql)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        return await comando.ExecuteScalarAsync();
    }

    /// <summary>
    /// Una base creada y migrada como antes de la plantilla y un clon tal como lo
    /// recibe un test (clonado y con su <c>MigrateAsync</c> ya llamado), con sus
    /// huellas. Viven lo que la clase y solo se leen.
    /// </summary>
    public sealed class LasDosBases : IAsyncLifetime
    {
        internal string CadenaMigrada { get; } = BaseDatosPostgresDePruebas.CadenaConexionDeBaseVacia();

        internal string CadenaDelClon { get; } = BaseDatosPostgresDePruebas.CadenaConexionUnica();

        internal IReadOnlyDictionary<string, string> HuellaMigrada { get; private set; } = null!;

        internal IReadOnlyDictionary<string, string> HuellaDelClon { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            await BaseDatosPostgresDePruebas.MigrarAsync(CadenaMigrada);
            await BaseDatosPostgresDePruebas.MigrarAsync(CadenaDelClon);

            HuellaMigrada = await HuellaDeBaseDeDatos.CalcularAsync(CadenaMigrada);
            HuellaDelClon = await HuellaDeBaseDeDatos.CalcularAsync(CadenaDelClon);
        }

        public async Task DisposeAsync()
        {
            await BaseDatosPostgresDePruebas.EliminarAsync(CadenaMigrada);
            await BaseDatosPostgresDePruebas.EliminarAsync(CadenaDelClon);
        }
    }
}
