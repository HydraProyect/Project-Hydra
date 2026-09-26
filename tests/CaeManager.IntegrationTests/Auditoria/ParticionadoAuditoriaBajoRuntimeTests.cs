using CaeManager.Infrastructure.Auditing;
using CaeManager.IntegrationTests.Arranque;
using CaeManager.Migrations.PostgreSQL;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// <b>El particionado mensual de los dos registros de auditoría no abre ninguna
/// puerta a <c>cae_app_runtime</c></b> (P1-M2, <see cref="ParticionadoMensualEventos"/>).
///
/// <para>
/// PostgreSQL comprueba privilegios y políticas de la tabla que la consulta
/// nombra: a través de la madre, los de la madre; nombrando una partición, los
/// de la partición. Así que la propiedad hay que probarla en las dos rutas, y en
/// TODAS las particiones —las que creó la migración, la de por defecto y las que
/// crea después <c>app_asegurar_particiones_eventos</c>—, no en una de muestra.
/// </para>
///
/// <para>
/// Conexión real como <c>cae_app_runtime</c> (login, no <c>SET ROLE</c>) sobre el
/// arnés de arranque; el contexto de Tenant se fija con la misma coordenada que
/// leen hoy las políticas.
/// </para>
/// </summary>
public class ParticionadoAuditoriaBajoRuntimeTests
{
    public static TheoryData<string, string> Tablas()
    {
        var datos = new TheoryData<string, string>();
        foreach (var (tabla, columna) in ParticionadoMensualEventos.Tablas)
            datos.Add(tabla, columna);
        return datos;
    }

    [Theory]
    [MemberData(nameof(Tablas))]
    public async Task Cada_particion_lleva_rls_forzada_las_politicas_de_la_madre_y_ningun_privilegio_de_aplicacion(
        string tabla, string columna)
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
        await propietario.OpenAsync();

        // Las particiones que crea la función después de la migración cuentan
        // igual que las de la migración: se fuerza una más allá del margen.
        await EjecutarAsync(propietario,
            $"SELECT * FROM app_asegurar_particiones_eventos({ParticionadoMensualEventos.MesesPorDelante + 2});");

        (await EscalarAsync<string>(propietario,
            $"SELECT relkind::text FROM pg_class WHERE oid = 'public.\"{tabla}\"'::regclass;"))
            .Should().Be("p", $"{tabla} tiene que estar particionada por {columna}");

        var politicasMadre = await PoliticasAsync(propietario, $"public.\"{tabla}\"");
        politicasMadre.Should().Contain(p => p.StartsWith("aislamiento_tenant|"),
            "control positivo: la madre conserva la política de aislamiento que copian las particiones");

        var particiones = await ParticionesAsync(propietario, tabla);
        particiones.Should().Contain($"{tabla}{ParticionadoMensualEventos.SufijoDefecto}");
        particiones.Count.Should().BeGreaterThanOrEqualTo(ParticionadoMensualEventos.MesesPorDelante + 4,
            "mes actual, los meses por delante, los dos forzados arriba y la de por defecto");

        using var _ = new AssertionScope();
        foreach (var particion in particiones)
        {
            var (habilitada, forzada) = await EstadoRlsAsync(propietario, particion);
            habilitada.Should().BeTrue($"{particion} tiene que tener RLS habilitada");
            forzada.Should().BeTrue($"{particion} tiene que tener RLS forzada");

            (await PoliticasAsync(propietario, $"public.\"{particion}\""))
                .Should().BeEquivalentTo(politicasMadre, $"{particion} tiene que llevar exactamente las políticas de {tabla}");

            foreach (var rol in new[] { "cae_app_runtime", "cae_app_soporte", "cae_app_aprovisionamiento" })
                foreach (var privilegio in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE" })
                {
                    (await EscalarAsync<bool>(propietario,
                        $"SELECT has_table_privilege('{rol}', 'public.\"{particion}\"', '{privilegio}');"))
                        .Should().BeFalse($"{rol} no debe tener {privilegio} sobre la partición {particion}");
                }
        }
    }

    [Theory]
    [MemberData(nameof(Tablas))]
    public async Task Runtime_escribe_en_varios_meses_por_la_madre_y_otro_Tenant_no_ve_nada(string tabla, string columna)
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var ahora = DateTime.UtcNow;
        // Mes actual, mes siguiente y uno sin partición (cae en la de por
        // defecto): las tres rutas de escritura que el particionado introduce.
        var fechas = new[] { ahora, ahora.AddMonths(1), ahora.AddYears(5) };

        await using var runtime = await AbrirComoRuntimeAsync(arnes);

        await FijarTenantAsync(runtime, tenantA);
        foreach (var fecha in fechas)
            await InsertarEventoAsync(runtime, tabla, tenantA, fecha);

        await FijarTenantAsync(runtime, tenantB);
        await InsertarEventoAsync(runtime, tabla, tenantB, ahora);

        (await ContarAsync(runtime, tabla, tenantA)).Should().Be(0,
            "bajo el contexto del Tenant B, las filas del Tenant A no existen en ninguna partición");
        (await ContarAsync(runtime, tabla, tenantB)).Should().Be(1);

        await FijarTenantAsync(runtime, tenantA);
        (await ContarAsync(runtime, tabla, tenantA)).Should().Be(fechas.Length,
            "control positivo: el Tenant A ve sus eventos de los tres meses, también el de la partición por defecto");
        (await ContarAsync(runtime, tabla, tenantB)).Should().Be(0);

        // Insertar en nombre de otro Tenant sigue rechazado por el WITH CHECK
        // de la madre (42501 de RLS), también hacia la partición por defecto.
        var insertarAjeno = () => InsertarEventoAsync(runtime, tabla, tenantB, ahora.AddYears(5));
        (await insertarAjeno.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");

        // Nombrar la partición no es una ruta alternativa: ni leer, ni escribir,
        // ni borrar.
        await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
        await propietario.OpenAsync();
        foreach (var particion in await ParticionesAsync(propietario, tabla))
        {
            foreach (var sentencia in new[]
            {
                $"SELECT count(*) FROM \"{particion}\";",
                $"DELETE FROM \"{particion}\";",
                $"UPDATE \"{particion}\" SET \"TenantId\" = \"TenantId\";",
            })
            {
                var intento = () => EjecutarAsync(runtime, sentencia);
                (await intento.Should().ThrowAsync<PostgresException>($"«{sentencia}» como runtime"))
                    .Which.SqlState.Should().Be("42501");
            }
        }

        // Y la madre sigue siendo de solo inserción.
        var borrar = () => EjecutarAsync(runtime, $"DELETE FROM \"{tabla}\";");
        (await borrar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
        var reescribir = () => EjecutarAsync(runtime, $"UPDATE \"{tabla}\" SET \"{columna}\" = \"{columna}\";");
        (await reescribir.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    [Fact]
    public async Task Runtime_puede_asegurar_las_particiones_pero_no_llamar_a_las_funciones_internas()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        await using var runtime = await AbrirComoRuntimeAsync(arnes);

        await EjecutarAsync(runtime, "SELECT * FROM app_asegurar_particiones_eventos(1);");

        foreach (var sentencia in new[]
        {
            "SELECT app_particion_eventos_asegurar('public.\"RegistrosAuditoria\"'::regclass, current_date + 3650);",
            "SELECT app_particion_eventos_proteger('public.\"RegistrosAuditoria\"'::regclass, 'public.\"RegistrosAuditoria\"'::regclass);",
            "SELECT app_rls_copiar_politicas('public.\"RegistrosAuditoria\"'::regclass, 'public.\"RegistrosAuditoria\"'::regclass);",
        })
        {
            var intento = () => EjecutarAsync(runtime, sentencia);
            (await intento.Should().ThrowAsync<PostgresException>(sentencia)).Which.SqlState.Should().Be("42501");
        }
    }

    [Fact]
    public async Task Asegurar_es_idempotente_y_mueve_a_su_mes_los_eventos_de_la_particion_por_defecto()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
        await propietario.OpenAsync();

        await EjecutarAsync(propietario, "SELECT * FROM app_asegurar_particiones_eventos(3);");
        var segunda = await AsegurarAsync(propietario, 3);
        segunda.Creadas.Should().Be(0, "una segunda llamada con el mismo margen no crea nada");

        // Un evento a seis meses vista: sin partición todavía, cae en la de por
        // defecto. Se escribe como propietario solo para fijar la fecha.
        var tenant = Guid.NewGuid();
        var id = Guid.NewGuid();
        var fecha = DateTime.UtcNow.AddMonths(6);
        await using (var orden = new NpgsqlCommand(
            """
            INSERT INTO "RegistrosAuditoria" ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "FechaUtc")
            VALUES (@id, @tenant, 'Empresa', @id, 'Creado', @fecha);
            """, propietario))
        {
            orden.Parameters.AddWithValue("id", id);
            orden.Parameters.AddWithValue("tenant", tenant);
            orden.Parameters.AddWithValue("fecha", fecha);
            await orden.ExecuteNonQueryAsync();
        }

        (await EscalarAsync<string>(propietario,
            $"SELECT tableoid::regclass::text FROM \"RegistrosAuditoria\" WHERE \"Id\" = '{id}';"))
            .Should().Be($"\"RegistrosAuditoria{ParticionadoMensualEventos.SufijoDefecto}\"",
                "control positivo: sin partición para su mes, el evento está en la de por defecto");
        (await AsegurarAsync(propietario, 3)).EnDefecto.Should().BeGreaterThan(0,
            "la función informa de eventos en la partición por defecto, que es la alarma del servicio diario");

        var resultado = await AsegurarAsync(propietario, 7);

        resultado.Creadas.Should().BeGreaterThan(0);
        resultado.EnDefecto.Should().Be(0, "el evento se movió a la partición de su mes");
        (await EscalarAsync<string>(propietario,
            $"SELECT tableoid::regclass::text FROM \"RegistrosAuditoria\" WHERE \"Id\" = '{id}';"))
            .Should().Be($"\"RegistrosAuditoria_p{fecha:yyyyMM}\"", "el evento no se pierde: cambia de partición");
    }

    /// <summary>
    /// El servicio diario, con la configuración que reciben staging y producción:
    /// <c>CaeManagerDb</c> vacía (P0-2: el contenedor <c>app</c> ya no tiene la
    /// credencial del propietario) y solo <c>CaeManagerDbRuntime</c>. Tiene que
    /// crear las particiones como <c>cae_app_runtime</c>, no fallar por una cadena
    /// vacía (hallazgo de Codex).
    /// </summary>
    [Fact]
    public async Task El_servicio_diario_funciona_con_la_configuracion_de_despliegue_solo_con_runtime()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CaeManagerDb"] = "",
                ["ConnectionStrings:CaeManagerDbRuntime"] = BaseDatosPostgresDePruebas.CadenaComoRuntime(arnes.CadenaPropietario),
            })
            .Build();
        var servicio = new ParticionesEventosHostedService(
            configuracion, new EntornoDePrueba("Production"), NullLogger<ParticionesEventosHostedService>.Instance);

        var resultado = await servicio.AsegurarAsync(CancellationToken.None);

        resultado.EventosEnDefecto.Should().Be(0);
        await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
        await propietario.OpenAsync();
        (await ParticionesAsync(propietario, "RegistrosAuditoria"))
            .Should().Contain($"RegistrosAuditoria_p{DateTime.UtcNow.AddMonths(ParticionesEventosHostedService.MesesPorDelante):yyyyMM}",
                "la llamada como runtime deja creado el último mes del margen");
    }

    /// <summary>
    /// Una escritura de auditoría del mes que se va a crear, en curso mientras
    /// la función mueve la partición por defecto, no puede hacerla fallar: la
    /// función espera a que confirme y la mueve también (hallazgo de Codex).
    /// </summary>
    [Fact]
    public async Task Asegurar_espera_a_una_insercion_concurrente_del_mes_y_la_mueve_tambien()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        await using var escritor = new NpgsqlConnection(arnes.CadenaPropietario);
        await escritor.OpenAsync();
        await using var mantenimiento = new NpgsqlConnection(arnes.CadenaPropietario);
        await mantenimiento.OpenAsync();

        // Todos los meses anteriores ya existen: el de la escritura concurrente
        // tiene que ser el PRIMERO que la función crea. Si no, la función espera
        // al crear un mes anterior, la escritura confirma mientras tanto y el
        // DELETE posterior ya la ve: la carrera no se abre y el test pasaría sin
        // el LOCK (medido por mutación).
        await AsegurarAsync(mantenimiento, 7);

        var fecha = DateTime.UtcNow.AddMonths(8);
        var previa = Guid.NewGuid();
        var concurrente = Guid.NewGuid();
        await InsertarComoPropietarioAsync(escritor, previa, fecha);

        await using var transaccion = await escritor.BeginTransactionAsync();
        await InsertarComoPropietarioAsync(escritor, concurrente, fecha);

        var pidMantenimiento = await EscalarAsync<int>(mantenimiento, "SELECT pg_backend_pid();");
        var asegurar = AsegurarAsync(mantenimiento, 9);

        // Barrera: la función está esperando un bloqueo, no ha terminado ni
        // fallado todavía.
        var esperando = false;
        for (var intento = 0; intento < 100 && !esperando && !asegurar.IsCompleted; intento++)
        {
            await Task.Delay(100);
            await using var consulta = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = @pid AND NOT granted);", escritor);
            consulta.Parameters.AddWithValue("pid", pidMantenimiento);
            esperando = (bool)(await consulta.ExecuteScalarAsync())!;
        }
        esperando.Should().BeTrue("control positivo: la función tiene que quedar esperando a la escritura en curso");

        await transaccion.CommitAsync();
        var resultado = await asegurar;

        resultado.EnDefecto.Should().Be(0);
        foreach (var id in new[] { previa, concurrente })
            (await EscalarAsync<string>(mantenimiento,
                $"SELECT tableoid::regclass::text FROM \"RegistrosAuditoria\" WHERE \"Id\" = '{id}';"))
                .Should().Be($"\"RegistrosAuditoria_p{fecha:yyyyMM}\"");
    }

    private static async Task InsertarComoPropietarioAsync(NpgsqlConnection conexion, Guid id, DateTime fecha)
    {
        await using var orden = new NpgsqlCommand(
            """
            INSERT INTO "RegistrosAuditoria" ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "FechaUtc")
            VALUES (@id, @id, 'Empresa', @id, 'Creado', @fecha);
            """, conexion);
        orden.Parameters.AddWithValue("id", id);
        orden.Parameters.AddWithValue("fecha", fecha);
        await orden.ExecuteNonQueryAsync();
    }

    private static async Task<(int Creadas, long EnDefecto)> AsegurarAsync(NpgsqlConnection conexion, int meses)
    {
        await using var orden = new NpgsqlCommand(
            "SELECT particiones_creadas, eventos_en_defecto FROM app_asegurar_particiones_eventos(@meses);", conexion);
        orden.Parameters.AddWithValue("meses", meses);
        await using var lector = await orden.ExecuteReaderAsync();
        await lector.ReadAsync();
        return (lector.GetInt32(0), lector.GetInt64(1));
    }

    private static async Task<NpgsqlConnection> AbrirComoRuntimeAsync(ArnesDeArranqueRuntime arnes)
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(arnes.CadenaPropietario));
        await conexion.OpenAsync();
        return conexion;
    }

    private static async Task FijarTenantAsync(NpgsqlConnection conexion, Guid tenant)
    {
        await using var orden = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, false);", conexion);
        orden.Parameters.AddWithValue("tenant", tenant.ToString());
        await orden.ExecuteNonQueryAsync();
    }

    private static async Task InsertarEventoAsync(NpgsqlConnection conexion, string tabla, Guid tenant, DateTime fecha)
    {
        var sql = tabla switch
        {
            "RegistrosAuditoria" => """
                INSERT INTO "RegistrosAuditoria" ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "FechaUtc")
                VALUES (@id, @tenant, 'Empresa', @id, 'Creado', @fecha);
                """,
            "RegistrosAccesoDocumentoSensible" => """
                INSERT INTO "RegistrosAccesoDocumentoSensible"
                    ("Id", "TenantId", "DocumentoId", "OcurridoEnUtc", "Sensibilidad", "TipoAcceso", "ViaAcceso")
                VALUES (@id, @tenant, @id, @fecha, 'Salud', 'Visualizacion', 'Directo');
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "tabla sin plantilla de inserción"),
        };
        await using var orden = new NpgsqlCommand(sql, conexion);
        orden.Parameters.AddWithValue("id", Guid.NewGuid());
        orden.Parameters.AddWithValue("tenant", tenant);
        orden.Parameters.AddWithValue("fecha", fecha);
        await orden.ExecuteNonQueryAsync();
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string tabla, Guid tenant)
    {
        await using var orden = new NpgsqlCommand($"SELECT count(*) FROM \"{tabla}\" WHERE \"TenantId\" = @tenant;", conexion);
        orden.Parameters.AddWithValue("tenant", tenant);
        return (long)(await orden.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> ParticionesAsync(NpgsqlConnection conexion, string tabla)
    {
        await using var orden = new NpgsqlCommand(
            "SELECT c.relname FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid " +
            "WHERE i.inhparent = format('public.%I', @tabla)::regclass ORDER BY c.relname;", conexion);
        orden.Parameters.AddWithValue("tabla", tabla);
        var particiones = new List<string>();
        await using var lector = await orden.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            particiones.Add(lector.GetString(0));
        return particiones;
    }

    private static async Task<(bool Habilitada, bool Forzada)> EstadoRlsAsync(NpgsqlConnection conexion, string relacion)
    {
        await using var orden = new NpgsqlCommand(
            "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE oid = format('public.%I', @relacion)::regclass;",
            conexion);
        orden.Parameters.AddWithValue("relacion", relacion);
        await using var lector = await orden.ExecuteReaderAsync();
        await lector.ReadAsync();
        return (lector.GetBoolean(0), lector.GetBoolean(1));
    }

    /// <summary>Nombre, comando, carácter y expresiones de cada política, para compararlas entre tablas.</summary>
    private static async Task<List<string>> PoliticasAsync(NpgsqlConnection conexion, string relacion)
    {
        await using var orden = new NpgsqlCommand(
            "SELECT polname || '|' || polcmd::text || '|' || polpermissive::text || '|' || polroles::text || '|' " +
            "|| coalesce(pg_get_expr(polqual, polrelid), '') || '|' || coalesce(pg_get_expr(polwithcheck, polrelid), '') " +
            "FROM pg_policy WHERE polrelid = @relacion::regclass ORDER BY polname;", conexion);
        orden.Parameters.AddWithValue("relacion", relacion);
        var politicas = new List<string>();
        await using var lector = await orden.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            politicas.Add(lector.GetString(0));
        return politicas;
    }

    private static async Task EjecutarAsync(NpgsqlConnection conexion, string sql)
    {
        await using var orden = new NpgsqlCommand(sql, conexion);
        await orden.ExecuteNonQueryAsync();
    }

    private static async Task<T> EscalarAsync<T>(NpgsqlConnection conexion, string sql)
    {
        await using var orden = new NpgsqlCommand(sql, conexion);
        return (T)(await orden.ExecuteScalarAsync())!;
    }
}
