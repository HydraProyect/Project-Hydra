using CaeManager.Application.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.ContextoRls;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.Migrations.PostgreSQL;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// P6 — contexto de sesión RLS firmado, contra PostgreSQL real y autenticando
/// como <c>cae_app_runtime</c> (no con <c>SET ROLE</c> desde el propietario).
///
/// <para>
/// La base del test se migra y después se le aplica
/// <see cref="ReescrituraPoliticasContextoRls"/>, que en la fase «expandir» no
/// corre en ningún entorno: es el estado al que llegará la fase «contraer», y
/// lo que aquí se demuestra es que, llegado ese estado, las cuatro propiedades
/// del encargo se cumplen y la reescritura cubre todas las políticas que
/// existen hoy.
/// </para>
///
/// <para>
/// Las cuatro propiedades: (1) una GUC falsificada sin token no devuelve
/// datos; (2) un token válido sí, y solo los de su Tenant; (3) un cruce de
/// Tenant falla, por GUC suelta o manipulando el token; (4) el contexto se
/// limpia al cerrar la transacción o la conexión.
/// </para>
/// </summary>
public class ContextoRlsFirmadoTests : IAsyncLifetime
{
    private readonly string _cadenaPropietario = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly List<Guid> _tenants = [];
    private int _politicasConGucAntesDeReescribir;
    private readonly FirmanteContextoRls _firmante = BaseDatosPostgresDePruebas.FirmanteContextoRls;
    private readonly Guid _actorPlataforma = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using (var contexto = CrearContextoPropietario())
        {
            await contexto.Database.MigrateAsync();

            for (var i = 0; i < 3; i++)
            {
                var tenant = new Tenant($"Tenant de prueba {i}");
                contexto.Tenants.Add(tenant);
                _tenants.Add(tenant.Id);
            }

            // Actor de Plataforma TALVEG con concesión global vigente: la
            // coordenada que un atacante con la credencial runtime querría
            // suplantar en el plano 3 (diseño § 6, propiedad 6).
            contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.Global(
                _actorPlataforma, vigenciaDesde: DateTime.UtcNow.AddMinutes(-10), vigenciaHasta: null));

            await contexto.SaveChangesAsync();
            await AsignacionesOperativasBackfillSeeder.SeedAsync(contexto, NullLogger.Instance);
        }

        _politicasConGucAntesDeReescribir = (await ExpresionesDePoliticasAsync())
            .Count(e => e.Expresion.Contains("current_setting('app.", StringComparison.Ordinal));
        await EjecutarComoPropietarioAsync(ReescrituraPoliticasContextoRls.Sql);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaPropietario);

    // ── (1) GUC falsificada sin token ─────────────────────────────────────

    [Fact]
    public async Task Una_GUC_falsificada_sin_token_no_devuelve_datos()
    {
        (await ContarRaicesComoPropietarioAsync()).Should().BeGreaterThan(1,
            "control negativo: hay una raíz por Tenant, así que un 0 abajo es RLS y no una tabla vacía");

        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarGucAsync(conexion, "app.tenant_id", _tenants[0].ToString());
        await FijarGucAsync(conexion, "app.tenant_origen_id", _tenants[0].ToString());
        await FijarGucAsync(conexion, "app.usuario_id", Guid.NewGuid().ToString());

        (await ContarRaicesAsync(conexion)).Should().Be(0,
            "tras la reescritura las políticas solo leen el contexto validado; las GUC sueltas que cualquier " +
            "sesión puede fijar ya no deciden nada");
    }

    // ── (2) Token válido ──────────────────────────────────────────────────

    [Fact]
    public async Task Un_token_valido_devuelve_solo_las_filas_de_su_Tenant()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarContextoAsync(conexion, Contexto(_tenants[0]));

        (await ContarRaicesAsync(conexion)).Should().Be(1);
        (await PropietarioDeLaUnicaRaizAsync(conexion)).Should().Be(_tenants[0],
            "el HMAC que calcula C# lo valida la función de PostgreSQL, y el Tenant que ve es el firmado");
    }

    [Fact]
    public async Task Sin_token_el_contexto_es_nulo_en_silencio()
    {
        await using var conexion = await AbrirComoRuntimeAsync();

        (await ContextoValidadoAsync(conexion)).Should().Be((null, null, null, false),
            "una conexión sin app.contexto (propietario, arranque, herramienta) no es un error: no ve nada");
    }

    // ── (3) Cruce de Tenant ───────────────────────────────────────────────

    [Fact]
    public async Task Con_token_valido_una_GUC_de_otro_Tenant_no_cruza()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarContextoAsync(conexion, Contexto(_tenants[0]));
        await FijarGucAsync(conexion, "app.tenant_id", _tenants[1].ToString());

        (await PropietarioDeLaUnicaRaizAsync(conexion)).Should().Be(_tenants[0],
            "la GUC suelta ya no es una coordenada de autoridad: sigue viendo el Tenant firmado");
    }

    [Fact]
    public async Task Manipular_el_Tenant_del_token_falla_con_42501()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        var token = await _firmante.FirmarAsync(conexion, Contexto(_tenants[0]), CancellationToken.None);
        var manipulado = token.Replace(_tenants[0].ToString("D"), _tenants[1].ToString("D"), StringComparison.Ordinal);
        manipulado.Should().NotBe(token);

        await FijarGucAsync(conexion, "app.contexto", manipulado);

        await DebeFallarCon42501Async(() => ContarRaicesAsync(conexion), "firma inválida");
    }

    [Fact]
    public async Task Un_token_firmado_para_otra_conexion_falla_con_42501()
    {
        await using var original = await AbrirComoRuntimeAsync();
        var token = await _firmante.FirmarAsync(original, Contexto(_tenants[0]), CancellationToken.None);

        await using var otra = await AbrirComoRuntimeAsync();
        otra.ProcessID.Should().NotBe(original.ProcessID);
        await FijarGucAsync(otra, "app.contexto", token);

        await DebeFallarCon42501Async(() => ContarRaicesAsync(otra), "otra conexión");
    }

    [Fact]
    public async Task Un_token_caducado_falla_con_42501()
    {
        // Reloj del firmante dos horas atrás: con TTL de 60 min el token nace
        // caducado respecto al now() de PostgreSQL.
        var firmanteAtrasado = new FirmanteContextoRls(
            BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool(),
            new RelojManual(DateTimeOffset.UtcNow.AddHours(-2)),
            FirmanteContextoRls.TtlPorDefecto, FirmanteContextoRls.RotacionPorDefecto);

        await using var conexion = await AbrirComoRuntimeAsync();
        var token = await firmanteAtrasado.FirmarAsync(conexion, Contexto(_tenants[0]), CancellationToken.None);
        await FijarGucAsync(conexion, "app.contexto", token);

        await DebeFallarCon42501Async(() => ContarRaicesAsync(conexion), "caducado");
    }

    [Fact]
    public async Task Basura_en_el_token_falla_con_42501()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarGucAsync(conexion, "app.contexto", "v1|lo-que-sea");

        await DebeFallarCon42501Async(() => ContarRaicesAsync(conexion), "formato inválido");
    }

    // ── (4) Limpieza ──────────────────────────────────────────────────────

    [Fact]
    public async Task Un_contexto_local_desaparece_al_cerrar_la_transaccion()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        var token = await _firmante.FirmarAsync(conexion, Contexto(_tenants[0]), CancellationToken.None);

        await using (var transaccion = await conexion.BeginTransactionAsync())
        {
            await using var fijar = conexion.CreateCommand();
            fijar.Transaction = transaccion;
            fijar.CommandText = "SELECT set_config('app.contexto', @t, true);";
            fijar.Parameters.AddWithValue("t", token);
            await fijar.ExecuteNonQueryAsync();

            (await ContarRaicesAsync(conexion, transaccion)).Should().Be(1, "control positivo dentro de la transacción");
            await transaccion.CommitAsync();
        }

        (await ContarRaicesAsync(conexion)).Should().Be(0, "set_config local muere con la transacción");
    }

    [Fact]
    public async Task Una_conexion_devuelta_al_pool_no_conserva_el_contexto()
    {
        // Pool propio (Application Name distinto = cadena distinta = pool
        // distinto) con un solo hueco: la segunda apertura tiene que ser el
        // mismo backend físico, no otro que estuviera ocioso.
        var cadenaRuntimeConPool = new NpgsqlConnectionStringBuilder(
            BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario))
        {
            ApplicationName = "p6-pool-reutilizado",
            MaxPoolSize = 1,
        }.ConnectionString;
        int pid;

        await using (var conexion = new NpgsqlConnection(cadenaRuntimeConPool))
        {
            await conexion.OpenAsync();
            pid = conexion.ProcessID;
            await FijarContextoAsync(conexion, Contexto(_tenants[0]));
            (await ContarRaicesAsync(conexion)).Should().Be(1, "control positivo antes de devolverla");
        }

        await using var reutilizada = new NpgsqlConnection(cadenaRuntimeConPool);
        await reutilizada.OpenAsync();
        reutilizada.ProcessID.Should().Be(pid,
            "el test solo demuestra algo si es el mismo backend físico sacado del pool");

        (await ContarRaicesAsync(reutilizada)).Should().Be(0,
            "Npgsql hace DISCARD ALL al reutilizar: el token de la petición anterior no llega a la siguiente");
    }

    // ── La clave: fuera del alcance del rol de tráfico ────────────────────

    [Theory]
    [InlineData("SELECT count(*) FROM app_privado.claves_contexto")]
    [InlineData("INSERT INTO app_privado.claves_contexto (id, ipad, opad, valida_hasta) VALUES (gen_random_uuid(), decode(repeat('00', 64), 'hex'), decode(repeat('00', 64), 'hex'), now() + interval '1 hour')")]
    [InlineData("DELETE FROM app_privado.claves_contexto")]
    public async Task El_rol_de_trafico_no_puede_leer_ni_escribir_las_claves(string sql)
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;

        var accion = () => comando.ExecuteNonQueryAsync();
        (await accion.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    // ── La reescritura: cubre todas las políticas que existen hoy ────────

    [Fact]
    public async Task Tras_la_reescritura_ninguna_politica_lee_las_GUC_sueltas()
    {
        var expresiones = await ExpresionesDePoliticasAsync();

        expresiones.Should().NotBeEmpty("control del instrumento: la consulta tiene que ver las políticas");
        expresiones.Where(e => e.Expresion.Contains("current_setting('app.", StringComparison.Ordinal))
            .Select(e => e.Politica).Should().BeEmpty();
        _politicasConGucAntesDeReescribir.Should().BeGreaterThan(0,
            "control del instrumento: antes de reescribir tiene que haber políticas leyendo app.*");
        expresiones.Count(e => e.Expresion.Contains("app_ctx_", StringComparison.Ordinal))
            .Should().Be(_politicasConGucAntesDeReescribir,
                "cada política que leía una GUC suelta pasa al contexto validado, ni una menos");
    }

    [Theory]
    [InlineData("ConcesionesPrivilegio")]
    [InlineData("EstadoBootstrapPlataforma")]
    [InlineData("OrdenMenuLateral")]
    public async Task Las_tablas_del_plano_de_plataforma_quedan_en_el_contexto_validado(string tabla)
    {
        var expresiones = (await ExpresionesDePoliticasAsync()).Where(e => e.Tabla == tabla).ToList();

        expresiones.Should().NotBeEmpty($"{tabla} tiene políticas");
        expresiones.Where(e => e.Expresion.Contains("app_ctx_", StringComparison.Ordinal)).Should().NotBeEmpty(
            $"las políticas de {tabla} que leían el usuario tienen que leerlo validado");
    }

    [Fact]
    public async Task La_excepcion_de_bootstrap_exige_contexto_firmado()
    {
        var expresiones = (await ExpresionesDePoliticasAsync())
            .Where(e => e.Tabla == "EstadoBootstrapPlataforma").Select(e => e.Expresion);

        expresiones.Should().Contain(e => e.Contains("app_ctx_valido()", StringComparison.Ordinal),
            "«nadie autenticado» pasa a «contexto firmado válido y sin usuario»: sin token no hay excepción");
    }

    [Fact]
    public async Task Con_token_la_escritura_en_otro_Tenant_la_rechaza_el_WITH_CHECK()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarContextoAsync(conexion, Contexto(_tenants[0]));

        await using var propio = conexion.CreateCommand();
        propio.CommandText = @"UPDATE ""AsignacionesOperacion"" SET ""PropietarioTenantId"" = ""PropietarioTenantId"" WHERE ""EsRaiz"";";
        (await propio.ExecuteNonQueryAsync()).Should().Be(1, "control positivo: su propia fila sí la actualiza");

        await using var ajeno = conexion.CreateCommand();
        ajeno.CommandText = @"UPDATE ""AsignacionesOperacion"" SET ""PropietarioTenantId"" = @otro WHERE ""EsRaiz"";";
        ajeno.Parameters.AddWithValue("otro", _tenants[1]);
        var accion = () => ajeno.ExecuteNonQueryAsync();
        (await accion.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501",
            "mover la fila a otro Tenant lo impide el WITH CHECK reescrito");
    }

    // ── Plano 3: suplantar a un Actor de Plataforma TALVEG ───────────────

    [Fact]
    public async Task Una_GUC_con_el_id_de_un_Actor_de_Plataforma_no_da_sus_privilegios()
    {
        await using (var propietario = new NpgsqlConnection(_cadenaPropietario))
        {
            await propietario.OpenAsync();
            (await ContarConcesionesAsync(propietario)).Should().Be(1,
                "control negativo: la concesión existe, así que un 0 abajo es RLS y no una tabla vacía");
        }

        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarGucAsync(conexion, "app.usuario_id", _actorPlataforma.ToString());

        (await ContarConcesionesAsync(conexion)).Should().Be(0,
            "la GUC suelta con el id del Actor de Plataforma TALVEG no le presta sus concesiones");
        var insertar = () => InsertarOrdenMenuAsync(conexion, _actorPlataforma);
        (await insertar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501",
            "el WITH CHECK de OrdenMenuLateral no ve un administrador de plataforma sin contexto firmado");
    }

    [Fact]
    public async Task Un_token_firmado_del_Actor_de_Plataforma_si_da_sus_privilegios()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarContextoAsync(conexion, new ContextoSesionRls(null, null, _actorPlataforma, OrigenContextoRls.Peticion));

        (await ContarConcesionesAsync(conexion)).Should().Be(1,
            "control positivo: la misma coordenada, firmada, sí es el Actor de Plataforma TALVEG");
        (await InsertarOrdenMenuAsync(conexion, _actorPlataforma)).Should().Be(1);
    }

    private static async Task<int> ContarConcesionesAsync(NpgsqlConnection conexion)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT count(*) FROM ""ConcesionesPrivilegio"";";
        return Convert.ToInt32(await comando.ExecuteScalarAsync());
    }

    private static async Task<int> InsertarOrdenMenuAsync(NpgsqlConnection conexion, Guid actor)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
INSERT INTO ""OrdenMenuLateral"" (""Id"", ""OrdenGrupos"", ""OrdenEnlaces"", ""ActualizadoPorUsuarioId"", ""ActualizadoEnUtc"", ""Version"")
VALUES (@id, ARRAY['control'], ARRAY[]::text[], @actor, now(), gen_random_uuid());";
        comando.Parameters.AddWithValue("id", OrdenMenuLateral.ClaveCanonica);
        comando.Parameters.AddWithValue("actor", actor);
        return await comando.ExecuteNonQueryAsync();
    }

    // ── El interceptor: firma de verdad, y renueva ────────────────────────

    [Fact]
    public async Task El_interceptor_envia_un_token_que_la_base_acepta()
    {
        await using var contexto = CrearContextoRuntime(_tenants[1], _firmante);

        var propietarios = await contexto.AsignacionesOperacion
            .Where(a => a.EsRaiz).Select(a => a.PropietarioTenantId).ToListAsync();

        propietarios.Should().Equal([_tenants[1]],
            "sin token la reescritura no dejaría ver nada; con uno ajeno, vería otra raíz");
    }

    /// <summary>
    /// Reloj del firmante 100 min atrás con TTL de 60: el token de apertura
    /// nace caducado para PostgreSQL. Adelantar el reloj más de TTL/2 obliga
    /// a renovar; <paramref name="enTransaccion"/> elige dónde: antes de un
    /// comando en autocommit, o antes del BEGIN (dentro de la transacción no
    /// se renueva, así que si el BEGIN no renovara, el comando fallaría).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task El_interceptor_renueva_el_token_antes_de_que_la_base_lo_de_por_caducado(bool enTransaccion)
    {
        var reloj = new RelojManual(DateTimeOffset.UtcNow.AddMinutes(-100));
        var firmante = new FirmanteContextoRls(
            BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool(), reloj,
            FirmanteContextoRls.TtlPorDefecto, FirmanteContextoRls.RotacionPorDefecto);

        await using var contexto = CrearContextoRuntime(_tenants[2], firmante);
        await contexto.Database.OpenConnectionAsync();
        try
        {
            var antes = () => contexto.AsignacionesOperacion.CountAsync(a => a.EsRaiz);
            (await antes.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501",
                "control del instrumento: sin renovar, el token de apertura está caducado");

            reloj.Adelantar(TimeSpan.FromMinutes(100));

            if (enTransaccion)
            {
                await using var transaccion = await contexto.Database.BeginTransactionAsync();
                (await contexto.AsignacionesOperacion.CountAsync(a => a.EsRaiz)).Should().Be(1,
                    "el interceptor renovó el token antes del BEGIN, que es cuando la base fija su now()");
            }
            else
            {
                (await contexto.AsignacionesOperacion.CountAsync(a => a.EsRaiz)).Should().Be(1,
                    "el interceptor renovó el token antes del comando");
            }
        }
        finally
        {
            await contexto.Database.CloseConnectionAsync();
        }
    }

    // ── Andamiaje ─────────────────────────────────────────────────────────

    private static ContextoSesionRls Contexto(Guid tenant) =>
        new(tenant, tenant, Guid.NewGuid(), OrigenContextoRls.Peticion);

    private async Task FijarContextoAsync(NpgsqlConnection conexion, ContextoSesionRls contexto)
    {
        var token = await _firmante.FirmarAsync(conexion, contexto, CancellationToken.None);
        await FijarGucAsync(conexion, "app.contexto", token);
    }

    private static async Task FijarGucAsync(NpgsqlConnection conexion, string guc, string valor)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT set_config(@guc, @valor, false);";
        comando.Parameters.AddWithValue("guc", guc);
        comando.Parameters.AddWithValue("valor", valor);
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task DebeFallarCon42501Async(Func<Task> accion, string motivo)
    {
        var error = (await accion.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be("42501");
        error.MessageText.Should().Contain(motivo, "el rechazo tiene que ser por la comprobación esperada, no por otra");
    }

    private static async Task<int> ContarRaicesAsync(NpgsqlConnection conexion, NpgsqlTransaction? transaccion = null)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = @"SELECT count(*) FROM ""AsignacionesOperacion"" WHERE ""EsRaiz"";";
        return Convert.ToInt32(await comando.ExecuteScalarAsync());
    }

    private static async Task<Guid> PropietarioDeLaUnicaRaizAsync(NpgsqlConnection conexion)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT ""PropietarioTenantId"" FROM ""AsignacionesOperacion"" WHERE ""EsRaiz"";";
        return (Guid)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<(Guid?, Guid?, Guid?, bool)> ContextoValidadoAsync(NpgsqlConnection conexion)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT tenant_id, tenant_origen_id, usuario_id, valido FROM app_contexto_validado();";
        await using var lector = await comando.ExecuteReaderAsync();
        await lector.ReadAsync();
        Guid? Leer(int i) => lector.IsDBNull(i) ? null : lector.GetGuid(i);
        return (Leer(0), Leer(1), Leer(2), lector.GetBoolean(3));
    }

    private async Task<int> ContarRaicesComoPropietarioAsync()
    {
        await using var conexion = new NpgsqlConnection(_cadenaPropietario);
        await conexion.OpenAsync();
        return await ContarRaicesAsync(conexion);
    }

    private sealed record ExpresionPolitica(string Tabla, string Politica, string Expresion);

    private async Task<List<ExpresionPolitica>> ExpresionesDePoliticasAsync()
    {
        await using var conexion = new NpgsqlConnection(_cadenaPropietario);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT cl.relname, pol.polname,
       coalesce(pg_get_expr(pol.polqual, pol.polrelid), '') || ' ' || coalesce(pg_get_expr(pol.polwithcheck, pol.polrelid), '')
  FROM pg_policy pol JOIN pg_class cl ON cl.oid = pol.polrelid;";
        var resultado = new List<ExpresionPolitica>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            resultado.Add(new ExpresionPolitica(lector.GetString(0), lector.GetString(1), lector.GetString(2)));
        return resultado;
    }

    private async Task EjecutarComoPropietarioAsync(string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadenaPropietario);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> AbrirComoRuntimeAsync()
    {
        // Sin pool: cada test quiere un backend propio (el pid va en el token).
        var cadena = new NpgsqlConnectionStringBuilder(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario))
        {
            Pooling = false,
        };
        var conexion = new NpgsqlConnection(cadena.ConnectionString);
        await conexion.OpenAsync();
        return conexion;
    }

    private CaeManagerDbContext CrearContextoPropietario()
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental());
    }

    private CaeManagerDbContext CrearContextoRuntime(Guid tenant, FirmanteContextoRls firmante)
    {
        var tenantActual = new TenantFijo(tenant);
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario))
            .AddInterceptors(new TenantRlsConnectionInterceptor(
                tenantActual, new SinClienteActivo(), new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: tenant),
                firmante))
            .Options;

        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class TenantFijo(Guid tenant) : ITenantActual
    {
        public Guid? TenantId => tenant;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class RelojManual(DateTimeOffset inicio) : TimeProvider
    {
        private DateTimeOffset _ahora = inicio;
        public void Adelantar(TimeSpan cuanto) => _ahora += cuanto;
        public override DateTimeOffset GetUtcNow() => _ahora;
    }
}
